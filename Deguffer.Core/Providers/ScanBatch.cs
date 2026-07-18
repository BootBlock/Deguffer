using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The sizes of one plan's paths, and the note the user must be shown about how they were obtained.
///
/// The two travel together deliberately. §5.5 requires the fallback to be observable, and a
/// provider that measured its paths but forgot to ask separately for the reason would silently drop
/// the elevation prompt with nothing to catch it.
/// </summary>
/// <param name="Sizes">One entry per path measured, in the order they were given.</param>
/// <param name="Note">The scan-route note, or null when the fast path served every path.</param>
public sealed record ScanBatch(IReadOnlyList<ScanSize> Sizes, PlanNote? Note)
{
    public ScanSize Total => Sizes.Aggregate(ScanSize.Zero, (sum, size) => sum + size);
}
