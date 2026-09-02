using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>One provider's contribution to the preview, present or not.</summary>
/// <param name="Provider">The provider that produced it.</param>
/// <param name="IsPresent">Whether the toolchain exists on this machine at all.</param>
/// <param name="Plan">
/// The dry run. Null when there was nothing to ask for one — an absent toolchain. A provider that
/// is absent only for want of an approved folder still carries a plan, because that plan holds the
/// sentence naming what to add.
/// </param>
public sealed record Finding(ICleanupProvider Provider, bool IsPresent, CleanupPlan? Plan)
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
