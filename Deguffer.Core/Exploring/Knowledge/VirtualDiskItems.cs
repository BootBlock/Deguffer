using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What Explore says about each virtual disk WSL and Docker Desktop record for this account: whose it
/// is, and the supported way to make it smaller.
///
/// <para>Explore draws one of these as a single large file and could say nothing more about it, while
/// the Storage page's report on the same disk named its owner and its route. The text comes from
/// each disk's <see cref="DiskOwner"/>, which the report reads too, so the two pages cannot disagree.</para>
/// </summary>
public static class VirtualDiskItems
{
    public static IReadOnlyList<KnownItem> For(VirtualDiskInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return [.. inventory.Disks.Select(disk => new KnownItem(
            KnownPlace.Discovered,
            disk.Path,
            disk.Owner.Summary(disk),
            disk.Owner.RouteInOneSentence(disk)))];
    }
}
