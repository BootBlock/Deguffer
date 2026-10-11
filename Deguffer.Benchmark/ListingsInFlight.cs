using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// A walk's listings, counted while they are in flight, so a run says how many folders it really
/// listed at once rather than only how many it was allowed to.
///
/// <para>Counted at the listing rather than in the walk's callback, because the listing is where a
/// worker spends its time, and the callback a worker reaches after it is over in moments. A walk
/// tuned for more threads than ever run at once is timed below the count its tuning names, and a
/// result that hides that compares the wrong thing.</para>
/// </summary>
internal sealed class ListingsInFlight(ListDirectory inner)
{
    private int _inFlight;
    private int _peak;

    /// <summary>The most listings that were in flight at once.</summary>
    public int Peak => Volatile.Read(ref _peak);

    public ListingEnd List(string directory, EnumerationOptions options, List<WalkEntry> into)
    {
        var now = Interlocked.Increment(ref _inFlight);
        RaisePeak(now);

        try
        {
            return inner(directory, options, into);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void RaisePeak(int now)
    {
        var peak = Volatile.Read(ref _peak);
        while (now > peak)
        {
            var was = Interlocked.CompareExchange(ref _peak, now, peak);
            if (was == peak)
            {
                return;
            }

            peak = was;
        }
    }
}
