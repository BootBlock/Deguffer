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
    private readonly InFlightPeak _listings = new();

    /// <summary>The most listings that were in flight at once.</summary>
    public int Peak => _listings.Peak;

    public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
    {
        using var listing = _listings.Enter();

        return inner(directory, request, into);
    }
}
