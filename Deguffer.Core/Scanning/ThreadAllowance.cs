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
/// </summary>
public sealed class ThreadAllowance
{
    // Never disposed: nothing asks it for the wait handle that disposal would free.
    private readonly SemaphoreSlim _places;

    /// <param name="places">How many threads may be at work at once across every run sharing it.</param>
    public ThreadAllowance(int places)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(places, 1);

        _places = new SemaphoreSlim(places, places);
        Places = places;
    }

    /// <summary>How many threads may be at work at once across every run sharing it.</summary>
    public int Places { get; }

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

    /// <summary>Give back a place taken by <see cref="TryTake"/>, <see cref="Take"/> or <see cref="TakeAsync"/>.</summary>
    public void Return() => _places.Release();
}
