namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The vendor's own route to a smaller disk for each kind, in the order its steps have to be taken,
/// shared by the Storage page's report and Explore's explanation so the two cannot come to disagree.
///
/// <para>§5.4 is why every route is two steps and says so. Deleting inside the guest frees space inside
/// the disk and none on the drive, and a user who prunes and sees no change stops trusting the figure.
/// <c>unreached-locations.md</c> §11 is why every route is the vendor's and none is Deguffer's:
/// compacting a disk that is in use or sparse can damage the whole disk, so Deguffer names the commands
/// and runs none of them.</para>
///
/// <para>Grounded in the vendors' own pages and release notes. WSL added
/// <c>wsl --manage --compact</c> in 3.0.1, and put <c>--set-sparse</c> behind <c>--allow-unsafe</c> in
/// 2.5.6 after reports of corruption. <c>Optimize-VHD</c> is in the Hyper-V module, needs the disk
/// detached, and refuses a sparse file. Docker Desktop 4.34 began giving unused space back by itself on
/// its managed disk, and 4.59 switched that off for the time being. Docker's Troubleshoot page offers
/// "Clean up data", which resets the disk.</para>
/// </summary>
public static class VirtualDiskRoutes
{
    /// <summary>The fact the whole report exists to state (§5.4).</summary>
    public const string InsideIsNotOnTheDrive =
        "Deleting files inside a virtual disk frees space inside it and none on this drive, until the disk is "
        + "compacted.";

    /// <summary>What Deguffer does with these disks, which is nothing (<c>unreached-locations.md</c> §11).</summary>
    public const string ReportOnly =
        "Deguffer only reports these disks. It never prunes, compacts or deletes one, because compacting a disk "
        + "that is in use, or a sparse one, can damage everything in it.";

    /// <summary>
    /// The route for one disk, in the order its steps are taken, as one or more sentences for the
    /// Storage page's report.
    /// </summary>
    /// <param name="isSparse">Whether the file is sparse, where that is known.</param>
    public static string For(VirtualDisk disk, bool isSparse)
    {
        ArgumentNullException.ThrowIfNull(disk);

        return disk.Kind switch
        {
            VirtualDiskKind.WslDistribution when isSparse =>
                $"To make it smaller, delete what is no longer needed inside '{disk.Distribution}', then run "
                + "'sudo fstrim -a' inside it: a sparse disk gives the discarded space back to the drive by itself. "
                + "Optimize-VHD refuses a sparse file.",

            VirtualDiskKind.WslDistribution =>
                $"To make it smaller, delete what is no longer needed inside '{disk.Distribution}' first, then compact "
                + $"the disk with 'wsl --manage {Quoted(disk.Distribution)} --compact' (WSL 3.0.1 and later), or on an "
                + "older WSL run 'wsl --shutdown' and then Optimize-VHD from the Hyper-V module. "
                + $"'wsl --manage {Quoted(disk.Distribution)} --set-sparse true' instead makes the disk give space back "
                + "by itself, and WSL 2.5.6 to 3.0.1 ask for '--allow-unsafe' with it after reports of damaged disks.",

            VirtualDiskKind.DockerData =>
                "To make it smaller, remove what Docker no longer needs first, with 'docker system prune' and "
                + "'docker builder prune'. Unused volumes hold data, and go only when '--volumes' is added. Docker "
                + "Desktop 4.34 and later give freed space back to the drive by themselves, which 4.59 turned off for "
                + "the time being. Docker Desktop's Troubleshoot page offers 'Clean up data', which resets the disk "
                + "and removes every container and image.",

            _ => "Docker Desktop makes this distribution itself and keeps it small, so there is nothing in it to "
                + "prune.",
        };
    }

    /// <summary>
    /// The route as one sentence, for Explore's verdict line, which states its point on one line.
    /// </summary>
    public static string InOneSentence(VirtualDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        return disk.Kind switch
        {
            VirtualDiskKind.WslDistribution =>
                "Deleting it deletes the distribution and everything in it, and the supported way to make it "
                + $"smaller is to delete files inside Linux and then compact it with 'wsl --manage {Quoted(disk.Distribution)} "
                + "--compact'.",

            VirtualDiskKind.DockerData =>
                "Deleting it removes every Docker image, container and volume, and the supported way to make it "
                + "smaller is 'docker system prune', while Docker Desktop's own 'Clean up data' empties it entirely.",

            _ => "Docker Desktop needs it to run and makes it itself, so there is nothing here to reclaim.",
        };
    }

    /// <summary>A distribution name as a command line needs it, quoted only where it has a space.</summary>
    private static string Quoted(string? distribution) =>
        distribution is { } name && name.Contains(' ') ? $"\"{name}\"" : distribution ?? string.Empty;
}
