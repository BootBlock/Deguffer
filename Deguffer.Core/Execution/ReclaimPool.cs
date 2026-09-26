using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Space that steps on more than one row each offer to free, so a total across rows counts it once.
///
/// <para>The component store is the case: its cleanup and its reset are separate rows, because the
/// reset's cost needs its own consent, and Windows states one overhead for the store that either
/// command frees from. Summed as two rows, a preview would offer that space twice, and choosing both
/// would promise back twice what the disk can give.</para>
///
/// <para>Only the preview's totals need this. The run measures what each command freed immediately
/// before and after it, so a reclaim is never counted twice there.</para>
/// </summary>
/// <param name="Name">Which space the steps share. Steps share a pool only where they name the same one.</param>
public sealed record ReclaimPool(string Name)
{
    /// <summary>
    /// What <paramref name="steps"/> reclaim together: every step outside a pool, and the largest step
    /// of each pool, since freeing the most any of them offers also frees what the others offer.
    /// </summary>
    public static ScanSize Total(IEnumerable<CleanupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var total = ScanSize.Zero;
        var pooled = new Dictionary<ReclaimPool, ScanSize>();

        foreach (var step in steps)
        {
            if (step.SharesReclaim is not { } pool)
            {
                total += step.Reclaim;
            }
            else if (!pooled.TryGetValue(pool, out var largest) || step.Reclaim.Reclaimable > largest.Reclaimable)
            {
                pooled[pool] = step.Reclaim;
            }
        }

        return pooled.Values.Aggregate(total, (sum, size) => sum + size);
    }
}
