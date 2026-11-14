using System.Runtime.CompilerServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Snapshots for a view that keeps a picture of memory current: on a fixed cadence while
/// <see cref="Live"/> is on, and only when <see cref="Refresh"/> asks while it is off.
///
/// <para><b>Never two reads at once, and never one straight after another on the cadence's
/// account.</b> The next read starts a full <see cref="Cadence"/> after the last one finished, however
/// long it took, so a machine slow to answer is asked less often rather than continuously (§7.2). A
/// read the reader asked for is the one exception to the second rule: it starts as soon as the read in
/// progress, if any, has finished.</para>
///
/// <para>Each read runs off the caller's thread. The process table alone is about a megabyte of
/// parsing, and the caller is a window.</para>
/// </summary>
public sealed class MemoryFeed(IMemorySource source, TimeProvider time)
{
    /// <summary>
    /// Two seconds: often enough that a program opening or closing shows up while the reader is still
    /// looking for it, and seldom enough that a picture of five hundred processes can be read at all.
    /// </summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Guards the fields below, which the caller's thread writes and the reading loop reads on
    /// the thread pool.
    /// </summary>
    private readonly Lock _gate = new();

    private bool _live = true;

    /// <summary>
    /// Whether the reader asked for a read the loop has not started yet. Several presses before it
    /// starts are one read, because the one read answers all of them.
    /// </summary>
    private bool _asked;

    /// <summary>
    /// Completed, and replaced, whenever <see cref="Live"/> or <see cref="_asked"/> changes, so a loop
    /// waiting for either wakes and reads them again.
    /// </summary>
    private TaskCompletionSource _changed = NewChange();

    /// <summary>
    /// Whether to read on the cadence. Off, a read happens only when <see cref="Refresh"/> asks for
    /// one.
    ///
    /// <para>Turned on, the feed reads at once: what is on screen was read when it was off, and may be
    /// as old as the reader's last press. Turned off, the cadence's next read does not happen, and a
    /// read already running finishes and is delivered, because it was started while the reader still
    /// wanted it.</para>
    /// </summary>
    public bool Live
    {
        get
        {
            lock (_gate)
            {
                return _live;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_live == value)
                {
                    return;
                }

                _live = value;
                _asked |= value;
            }

            Wake();
        }
    }

    private DateTimeOffset? _lastRead;

    /// <summary>When the last snapshot this feed delivered was read, or null before the first.</summary>
    public DateTimeOffset? LastRead
    {
        get
        {
            // Under the lock because the loop writes it on the thread pool, and a nullable
            // DateTimeOffset is wider than one write the processor makes whole.
            lock (_gate)
            {
                return _lastRead;
            }
        }
    }

    /// <summary>
    /// Ask for a read now, live or not. One already running is left to finish, and this one starts as
    /// soon as it has, so the snapshot delivered is one read after the reader asked.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _asked = true;
        }

        Wake();
    }

    /// <summary>
    /// A snapshot now, and another each time <see cref="Live"/>'s cadence or <see cref="Refresh"/>
    /// calls for one, until <paramref name="ct"/> is cancelled. Cancelling ends the sequence with
    /// <see cref="OperationCanceledException"/> and starts no further read.
    ///
    /// <para>Begun again while <see cref="Live"/> is off, by a page the reader has come back to, the
    /// first read waits for <see cref="Refresh"/> as well, where this feed has delivered one already:
    /// the reader turned the cadence off so that the figures would change only when they asked, and
    /// coming back to the page is not asking.</para>
    /// </summary>
    public async IAsyncEnumerable<MemorySnapshot> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (LastRead is not null && !Live)
        {
            await NextAsync(ct).ConfigureAwait(false);
        }

        while (true)
        {
            lock (_gate)
            {
                // Before the read rather than after it, so a press during the read asks for the next
                // one: the read already running was begun before the reader asked.
                _asked = false;
            }

            var snapshot = await Task.Run(() => source.Read(ct), ct).ConfigureAwait(false);

            lock (_gate)
            {
                _lastRead = time.GetUtcNow();
            }

            yield return snapshot;

            await NextAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wait until the next read is due: a whole cadence while live, or the reader asking for one.
    /// </summary>
    private async Task NextAsync(CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            bool live;

            lock (_gate)
            {
                if (_asked)
                {
                    return;
                }

                changed = _changed.Task;
                live = _live;
            }

            if (!live)
            {
                await changed.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            // The beat is called off when something else wakes the loop first, so a page whose reader
            // presses Refresh does not leave a timer behind it for every press.
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var due = Task.Delay(Cadence, time, beat.Token);

            var first = await Task.WhenAny(due, changed).ConfigureAwait(false);

            await beat.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (first == due)
            {
                return;
            }
        }
    }

    private void Wake()
    {
        TaskCompletionSource changed;

        lock (_gate)
        {
            changed = _changed;
            _changed = NewChange();
        }

        changed.TrySetResult();
    }

    /// <summary>
    /// Continued on the thread pool rather than inside <see cref="Wake"/>, so the caller's thread, which
    /// is the window's, never runs the reading loop.
    /// </summary>
    private static TaskCompletionSource NewChange() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
