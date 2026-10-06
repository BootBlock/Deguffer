using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What Explore says about each virtual disk WSL and Docker Desktop record for this account: whose it
/// is, and the supported way to make it smaller.
///
/// <para>Explore draws one of these as a single large file and could say nothing more about it, while
/// the Storage page's report on the same disk named its owner and its route. The text comes from
/// <see cref="VirtualDiskRoutes"/>, which the report reads too, so the two pages cannot disagree.</para>
/// </summary>
public static class VirtualDiskItems
{
    public static IReadOnlyList<KnownItem> For(VirtualDiskInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return [.. inventory.Disks.Select(disk => new KnownItem(
            KnownPlace.Discovered,
            disk.Path,
            Summary(disk),
            VirtualDiskRoutes.InOneSentence(disk)))];
    }

    private static string Summary(VirtualDisk disk) => disk.Kind switch
    {
        VirtualDiskKind.WslDistribution =>
            $"The virtual disk of the WSL distribution '{disk.Distribution}': its whole Linux file system, home "
            + "folders included. It grows as files are written inside Linux and does not shrink by itself. "
            + VirtualDiskRoutes.InsideIsNotOnTheDrive,

        VirtualDiskKind.DockerData =>
            "Docker Desktop's data disk: every image, container, volume and build cache Docker keeps. It grows as "
            + "Docker writes and does not shrink by itself. " + VirtualDiskRoutes.InsideIsNotOnTheDrive,

        _ => "The virtual disk of Docker Desktop's own Linux distribution, which runs the Docker engine.",
    };
}
