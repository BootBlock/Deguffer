namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The text every virtual disk's route shares, read by each <see cref="DiskOwner"/> and by the Storage
/// page's report, so the report and Explore's explanation cannot come to disagree.
///
/// <para>§5.4 is why every route is two steps and says so. Deleting inside the guest frees space inside
/// the disk and none on the drive, and a user who prunes and sees no change stops trusting the figure.
/// <c>unreached-locations.md</c> §11 is why every route is the vendor's and none is Deguffer's:
/// compacting a disk that is in use or sparse can damage the whole disk, so Deguffer names the commands
/// and runs none of them.</para>
///
/// <para>Grounded in the vendors' own pages and release notes. WSL added
/// <c>wsl --manage --compact</c> in 3.0.1, and put <c>--set-sparse</c> behind <c>--allow-unsafe</c> in
/// 2.5.6 after reports of corruption. <c>Optimize-VHD</c> is in the Hyper-V module and needs the disk
/// detached or attached read-only (its cmdlet page), and the module refuses an unelevated user outside
/// the Hyper-V Administrators group (observed). No Microsoft page says how it treats a sparse file, so the
/// route for a sparse disk is the one that needs no compaction. Each owner's own steps are in its
/// <see cref="DiskOwner"/>.</para>
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
    /// What every Hyper-V cmdlet needs, the compaction of a WSL disk on an older WSL and every Hyper-V
    /// machine's route alike, as the end of a sentence whose subject is the cmdlets.
    /// </summary>
    public const string HyperVModuleNeeds =
        "need an elevated PowerShell or membership of the Hyper-V Administrators group";

    /// <summary>
    /// What follows the owner's own clean-up for any disk WSL keeps: the compaction that gives the space
    /// back to the drive, which is WSL's whoever registered the distribution. Two sentences or more,
    /// each beginning with its step.
    /// </summary>
    /// <param name="isSparse">Whether the file is sparse, where that is known.</param>
    public static string WslCompaction(VirtualDisk disk, bool isSparse)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var name = Quoted(disk.Distribution);

        return isSparse
            ? " Then run 'sudo fstrim -a' inside it: a sparse disk gives the discarded space back to the drive by "
                + "itself."
            : $" Then compact the disk with 'wsl --manage {name} --compact' (WSL 3.0.1 and later), or on an older WSL "
                + "run 'wsl --shutdown' and then Optimize-VHD from the Hyper-V module. Its cmdlets "
                + HyperVModuleNeeds + ". "
                + $"'wsl --manage {name} --set-sparse true' instead makes the disk give space back by itself, and WSL "
                + "2.5.6 to 3.0.1 ask for '--allow-unsafe' with it after reports of damaged disks.";
    }

    /// <summary>A distribution name as a command line needs it, quoted only where it has a space.</summary>
    public static string Quoted(string? distribution) =>
        distribution is { } name && name.Contains(' ') ? $"\"{name}\"" : distribution ?? string.Empty;
}
