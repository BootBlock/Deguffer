using Deguffer.Core.Safety;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The disks each disk is built on, as Hyper-V describes them: a checkpoint's <c>.avhdx</c> is built on
/// the disk before it, and so on down to a disk built on nothing. Deleting any disk in the chain breaks
/// every disk above it, so each is registered.
///
/// <para>Each disk is described once a reading, so a parent two machines share, or a chain several
/// checkpoints share, is asked about once. A disk Hyper-V would not describe is unfollowed: the chain
/// below it is not known, and the caller says so rather than hiding it.</para>
/// </summary>
internal sealed class HyperVDiskChains(IHyperVSession session, CancellationToken ct)
{
    /// <summary>Each disk described so far, with what it is built on, or null for nothing.</summary>
    private readonly Dictionary<string, string?> _parents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each disk Hyper-V would not describe, so it is not asked about twice.</summary>
    private readonly HashSet<string> _undescribed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Follows each of <paramref name="disks"/> down its chain, adding each disk it is built on to
    /// <paramref name="parents"/> and each disk whose parent is not known to <paramref name="unfollowed"/>.
    /// A chain that comes back to a disk already in it ends there, so a damaged chain cannot hold the
    /// reading for ever.
    /// </summary>
    public void Follow(IEnumerable<string> disks, List<string> parents, List<string> unfollowed)
    {
        foreach (var disk in disks)
        {
            HashSet<string> chain = new(StringComparer.OrdinalIgnoreCase) { disk };
            var next = disk;

            while (true)
            {
                if (!TryParent(next, out var parent))
                {
                    unfollowed.Add(next);
                    break;
                }

                if (parent is null || !chain.Add(parent))
                {
                    break;
                }

                parents.Add(parent);
                next = parent;
            }
        }
    }

    /// <summary>
    /// Whether Hyper-V described <paramref name="disk"/>, with what it is built on in
    /// <paramref name="parent"/>. A parent that is not a full path is no answer, because a disk cannot be
    /// registered at it.
    /// </summary>
    private bool TryParent(string disk, out string? parent)
    {
        if (_parents.TryGetValue(disk, out parent))
        {
            return true;
        }

        if (_undescribed.Contains(disk))
        {
            return false;
        }

        var answer = session.DescribeDisk(disk, ct);

        if (answer is { Error: WbemConnection.S_OK, ReturnValue: 0 }
            && HyperVDiskSettingData.TryReadParent(answer.SettingData, out var named))
        {
            parent = named is null ? null : LongPath.Configured(named);

            if (named is null || parent is not null)
            {
                _parents[disk] = parent;
                return true;
            }
        }

        _undescribed.Add(disk);
        parent = null;
        return false;
    }
}
