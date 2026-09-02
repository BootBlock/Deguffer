using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>One provider's contribution to the preview, present or not.</summary>
/// <param name="Provider">The provider that produced it.</param>
/// <param name="IsPresent">
/// Whether the provider found what it manages. For a cache provider that is the toolchain; for one
/// that searches the user's source trees it is whether it has anywhere approved to search, which is
/// why <paramref name="AwaitingSourceFolders"/> exists to tell the two apart.
/// </param>
/// <param name="Plan">
/// The dry run. Null when there was nothing to ask for one — an absent toolchain. A provider that
/// is absent only for want of an approved folder still carries a plan, because that plan holds the
/// sentence naming what to add.
/// </param>
/// <param name="AwaitingSourceFolders">
/// Whether this provider searches only folders the user has approved and has none, so the row has
/// something to ask for rather than something to report. Recorded here rather than re-asked of the
/// provider later: approving a folder changes the provider's answer, and a row is a description of
/// the scan that produced it.
/// </param>
public sealed record Finding(
    ICleanupProvider Provider,
    bool IsPresent,
    CleanupPlan? Plan,
    bool AwaitingSourceFolders = false)
{
    public long EstimatedBytes => Plan?.EstimatedBytes ?? 0;

    /// <summary>
    /// The same total with both numbers and the approximation flag intact, for the label the user
    /// reads. <see cref="EstimatedBytes"/> stays the number to sort and compare by.
    /// </summary>
    public ScanSize Estimated => Plan?.Estimated ?? ScanSize.Zero;

    /// <summary>Whether there is anything here worth showing the user as reclaimable.</summary>
    public bool HasReclaimableSpace => EstimatedBytes > 0;

    /// <summary>
    /// §3's "Default" column: only Tier 1 is pre-selected, and only when there is something to
    /// reclaim. This lives here rather than in the view-model so the tier table is answerable in
    /// one place.
    /// </summary>
    public bool IsPreSelectedByDefault => HasReclaimableSpace && Provider.Tier.IsPreSelectedByDefault();
}
