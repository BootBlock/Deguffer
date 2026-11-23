using System.Runtime.ExceptionServices;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Makes a known number of reads, numbered in the order their results are wanted, a fixed number at
/// once, and gives each result to the calling thread in that order, whatever order they land in.
///
/// <para><b>A read that lands starts the next one</b>, on the thread it landed on, so the disk is
/// asked for every read in flight whichever of them is slow. A calling thread waiting for the oldest
/// before starting another would let every read behind it finish with nothing to replace it.</para>
///
/// <para><b>Each place for a read in flight makes one read at a time</b>, so whatever a read keeps per
/// place, a buffer above all, is never shared, and is free again once the read has landed.</para>
///
/// <para><b>Reads go at most a window past the oldest not yet handed on</b>, so what waits for its turn
/// is bounded however slow that read is.</para>
///
/// <para><b>Nothing is left in flight.</b> <see cref="Dispose"/> stops new reads, cancels those in
/// flight and waits for them, so whatever they read into can be released after it.</para>
/// </summary>
internal sealed class OrderedReads : IDisposable
{
    private readonly Func<int, int, CancellationToken, Task> _land;
    private readonly int _count;
    private readonly int _ahead;
    private readonly CancellationToken _ct;
    private readonly CancellationTokenSource _cancelReads;
    private readonly CancellationTokenRegistration _wake;

    /// <summary>The run of reads made in each place, written only on the calling thread.</summary>
    private readonly Task?[] _workers;

    /// <summary>Each read once it has landed, whether it succeeded or not.</summary>
    private readonly Task?[] _landed;

    /// <summary>Places with no read in flight, waiting for a read the window allows.</summary>
    private readonly Stack<int> _idle;

    // Monitor rather than Lock: the calling thread waits on it for the read whose turn it is.
    private readonly object _gate = new();
    private int _next;
    private int _handing;
    private bool _stopping;

    /// <param name="count">How many reads there are.</param>
    /// <param name="places">How many are in flight at once, at most.</param>
    /// <param name="ahead">How far past the oldest read not yet handed on a read may be made, at least one.</param>
    /// <param name="land">
    /// Make a read in a place, given the place, the read and a token cancelled when nothing more will be
    /// read. Its task ends once the read's result is kept for its turn.
    /// </param>
    public OrderedReads(int count, int places, int ahead, Func<int, int, CancellationToken, Task> land, CancellationToken ct)
    {
        _land = land;
        _count = count;
        _ahead = ahead;
        _ct = ct;
        _workers = new Task?[places];
        _landed = new Task?[count];
        _idle = new Stack<int>(places);
        _cancelReads = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // A read that ignores the token still lands, and a calling thread woken only by that would hand
        // it on after the cancellation. Woken by the token, it throws instead.
        _wake = ct.Register(static state => ((OrderedReads)state!).Wake(), this);

        for (var place = 0; place < places; place++)
        {
            Start(place);
        }
    }

    /// <summary>Wait for <paramref name="read"/> to land, and throw what it failed with.</summary>
    public void Await(int read)
    {
        Task landing;

        lock (_gate)
        {
            while (_landed[read] is null)
            {
                _ct.ThrowIfCancellationRequested();
                Monitor.Wait(_gate);
            }

            landing = _landed[read]!;
        }

        if (landing.Exception is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
        }

        if (landing.IsCanceled)
        {
            throw new TaskCanceledException(landing);
        }
    }

    /// <summary>Say the oldest read has been handed on, so the window moves past it.</summary>
    public void HandedOn()
    {
        lock (_gate)
        {
            _handing++;
        }

        // A place that stopped at the edge of the window can read again now it has moved.
        while (TryResume(out var place))
        {
            Start(place);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
        }

        _wake.Dispose();
        _cancelReads.Cancel();

        foreach (var worker in _workers)
        {
            // Each read's failure is the calling thread's to throw at its turn, so a run of reads never
            // fails itself. Waited for so nothing is still landing in what the caller releases next.
            worker?.Wait();
        }

        _cancelReads.Dispose();
    }

    /// <summary>Make reads in <paramref name="place"/> until there is none the window allows.</summary>
    private void Start(int place)
    {
        int read;

        lock (_gate)
        {
            if (!TryClaim(out read))
            {
                _idle.Push(place);
                return;
            }
        }

        // Written before this place can be resumed, which only this thread does. A run it replaces had
        // nothing left to do but return.
        _workers[place] = WorkAsync(place, read);
    }

    private async Task WorkAsync(int place, int read)
    {
        while (true)
        {
            var landing = _land(place, read, _cancelReads.Token);

            // Thrown on the calling thread at the read's turn. Thrown here, on a thread the system
            // completed the read on, nothing would ever see it.
            await landing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            // Read so a failure past where the calling thread stopped is not reported later as one
            // nobody saw. One it does reach is thrown there again.
            _ = landing.Exception;

            lock (_gate)
            {
                _landed[read] = landing;

                if (read == _handing)
                {
                    Monitor.Pulse(_gate);
                }

                if (!TryClaim(out read))
                {
                    _idle.Push(place);
                    return;
                }
            }
        }
    }

    /// <summary>The next read, where the window allows one and nothing is stopping. Called holding the gate.</summary>
    private bool TryClaim(out int read)
    {
        read = _next;

        if (!HasRoom() || _cancelReads.IsCancellationRequested)
        {
            return false;
        }

        _next++;
        return true;
    }

    /// <summary>A place with no read in flight, where the window has room for one. A run of reads already in flight may still claim that room first.</summary>
    private bool TryResume(out int place)
    {
        lock (_gate)
        {
            place = 0;
            return HasRoom() && _idle.TryPop(out place);
        }
    }

    private bool HasRoom() => !_stopping && _next < _count && _next < _handing + _ahead;

    private void Wake()
    {
        lock (_gate)
        {
            Monitor.Pulse(_gate);
        }
    }
}
