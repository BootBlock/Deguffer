using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What Explore says about each virtual disk a tool records for this account: whose it is, and the
/// supported way to make it smaller.
///
/// <para>Explore draws one of these as a large file, or a split disk as several, and could say nothing
/// more about them, while
/// the Storage page's report on the same disk named its owner and its route. The text comes from
/// each disk's <see cref="DiskOwner"/>, which the report reads too, so the two pages cannot disagree.</para>
/// </summary>
public static class VirtualDiskItems
{
    public static IReadOnlyList<KnownItem> For(VirtualDiskInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        // Every file of a split disk, not only its descriptor: the extents are the large files the size
        // picture draws, and the descriptor beside them is a kilobyte.
        return [.. inventory.Disks.SelectMany(disk => (IEnumerable<string>)[disk.Path, .. disk.Parts ?? []], (disk, path) => new KnownItem(
            KnownPlace.Discovered,
            path,
            disk.Owner.Summary(disk),
            disk.Owner.RouteInOneSentence(disk)))];
    }
}
