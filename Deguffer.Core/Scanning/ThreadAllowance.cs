namespace Deguffer.Core.Scanning;

/// <summary>
/// The most threads several parallel runs may have at work at once between them, where each run has a
/// bound of its own as well. A thread takes a place before it starts on the work the allowance counts,
/// and gives it back when it stops.
///
/// <para><b>The removals of a clean are the case.</b> A removal deletes with up to
/// <see cref="Execution.DirectoryRemover.MostAtOnce"/> threads, a bound chosen so one removal does not
/// flood the disk's queue. Several removals that ran at once with that bound each would multiply the
/// queue by their number, so the steps of a plan that run together share one allowance of that size
/// and the queue is no deeper than one removal makes it. See <see cref="Execution.PlanExecutor"/>.</para>
///
/// <para>A run that takes no place from an allowance shared with nothing is bounded by its own crew
/// alone, so a removal outside a clean is given an allowance of its own.</para>
///
/// <para><b>A place given back is offered at once.</b> A crew asks for a helper's place when work
/// arrives, and work that arrived while every place was taken would otherwise wait for the threads
/// the crew already has. A large tree that started while small ones held every place would then be
/// removed by one thread from start to end, where alone it would have had the whole bound. So a place
/// that comes back is offered to the listening runs until one that was refused a place takes it: see
/// <see cref="WhenReturned"/>.</para>
/// </summary>
public sealed class ThreadAllowance
{
    // Never disposed: nothing asks it for the wait handle that disposal would free.
    private readonly SemaphoreSlim _places;
    private readonly Lock _gate = new();

    // Replaced whole under the lock and read without it, so a place given back never waits for a
    // run joining or leaving.
    private Func<bool>[] _offers = [];

    // Where the next place given back is first offered, moved on each time, so no run is always asked
    // last.
    private int _nextOffer;

    /// <param name="places">How many threads may be at work at once across every run sharing it.</param>
    public ThreadAllowance(int places)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(places, 1);

        _places = new SemaphoreSlim(places, places);
    }

    /// <summary>Take a place if one is free now, without waiting. True where one was taken.</summary>
    public bool TryTake() => _places.Wait(0);

    /// <summary>
    /// Take a place, waiting for one to be given back where none is free. False, with no place taken,
    /// where <paramref name="ct"/> is cancelled first. For a thread of its own, which the wait blocks.
    /// </summary>
    public bool Take(CancellationToken ct)
    {
        try
        {
            _places.Wait(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller is stopping, and a place it was never given is one it must not give back.
            return false;
        }
    }

    /// <summary>
    /// Take a place, waiting without a thread for one to be given back.
    /// <see cref="OperationCanceledException"/> where <paramref name="ct"/> is cancelled first.
    /// </summary>
    public Task TakeAsync(CancellationToken ct) => _places.WaitAsync(ct);

    /// <summary>
    /// Give back a place taken by <see cref="TryTake"/>, <see cref="Take"/> or <see cref="TakeAsync"/>,
    /// and offer it to the runs listening through <see cref="WhenReturned"/> until one takes it. One place
    /// can go to one run, so the rest are not asked: each asking takes the semaphore's lock, and a
    /// removal's helpers give places back many times a second.
    /// </summary>
    public void Return()
    {
        _places.Release();

        var offers = Volatile.Read(ref _offers);
        var first = offers.Length == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _nextOffer) % (uint)offers.Length);

        for (var i = 0; i < offers.Length; i++)
        {
            if (offers[(first + i) % offers.Length]())
            {
                return;
            }
        }
    }

    /// <summary>
    /// Call <paramref name="offer"/> when a place is given back, until the result is disposed, and stop
    /// offering that place where it answers that it took one. The offer runs on the thread giving the
    /// place back, so it must not block or throw: a crew answers with
    /// <see cref="WorkerCrew.OfferPlace"/>, which does neither.
    /// </summary>
    internal IDisposable WhenReturned(Func<bool> offer)
    {
        lock (_gate)
        {
            _offers = [.. _offers, offer];
        }

        return new Listening(this, offer);
    }

    private void StopOffering(Func<bool> offer)
    {
        lock (_gate)
        {
            _offers = [.. _offers.Where(listening => !ReferenceEquals(listening, offer))];
        }
    }

    private sealed class Listening(ThreadAllowance allowance, Func<bool> offer) : IDisposable
    {
        public void Dispose() => allowance.StopOffering(offer);
    }
}
