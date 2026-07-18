namespace Deguffer.Core.Scanning;

/// <summary>
/// What a tree occupies, as the two numbers that can legitimately differ.
///
/// <paramref name="Logical"/> is the sum of file lengths — what Explorer calls "Size", and what
/// re-downloading would cost. <paramref name="Allocated"/> is what the volume actually gives back
/// on deletion. They diverge on NTFS-compressed and sparse files, where a cache directory can
/// report several gigabytes logically while freeing a fraction of that. Reporting only the logical
/// number would overstate the reclaim on exactly the trees most worth reclaiming.
///
/// This is *not* §5.4's pair. That one — space freed inside a VHDX versus on the host — cannot be
/// measured from the filesystem at all; it comes from the container tool's own accounting, so it
/// belongs to a provider, not to scanning.
/// </summary>
/// <param name="IsApproximate">
/// True when the numbers came from a source that cannot distinguish the two (§5.5's fallback path
/// reports file lengths only). Carried so the UI can say so rather than implying precision the
/// measurement does not have.
/// </param>
public readonly record struct ScanSize(long Allocated, long Logical, bool IsApproximate = false)
{
    public static readonly ScanSize Zero = new(0, 0);

    /// <summary>
    /// A measurement from a source that only knows file lengths. Allocated is set equal to logical
    /// rather than zero: callers subtract these to report reclaim, and a zero would read as
    /// "nothing to gain" instead of "not known precisely".
    /// </summary>
    public static ScanSize Approximate(long logical) => new(logical, logical, IsApproximate: true);

    /// <summary>Approximation is contagious: a total is only as exact as its least exact part.</summary>
    public static ScanSize operator +(ScanSize left, ScanSize right) => new(
        left.Allocated + right.Allocated,
        left.Logical + right.Logical,
        left.IsApproximate || right.IsApproximate);

    /// <summary>
    /// The single number to show and to subtract when reporting reclaim. Allocated is the honest
    /// answer to "how much space do I get back", which is the question §7 says the user came for.
    /// </summary>
    public long Reclaimable => Allocated;
}
