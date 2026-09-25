namespace Deguffer.Core.Safety;

/// <summary>
/// Where a path sits relative to the top of its own volume.
///
/// <para><b>The top is where the volume is really mounted, asked of the machine each time.</b> A
/// drive letter is not the only place a volume can be mounted: one mounted at <c>C:\Mount</c> has
/// its paging file at <c>C:\Mount\pagefile.sys</c>, and reading the position from the drive letter
/// put that one level below <c>C:\</c> — so the rules that keep a volume's own system files out of
/// a deletion did not recognise it. <see cref="IVolumeInventory.MountPointOf"/> answers live rather
/// than from the remembered list, because the callers have to be right on a drive plugged in a
/// moment ago, and a list is a snapshot.</para>
///
/// <para><b>Where the machine gives no position, the drive letter does.</b> Windows answers nothing
/// for a drive that is not there and for a share it cannot reach, and a path through a junction is
/// answered with the volume on the junction's far side, which is not a prefix of the path and so
/// names no position in it. Each of those is read from <see cref="Path.GetPathRoot(string)"/>
/// alone, which is the answer this type gave before it asked the machine.</para>
///
/// <para>Its callers are the policy that refuses to delete a volume's paging file or NTFS's own
/// records, and the reference that explains to a reader what those are. A safety rule written twice
/// is one that gets changed once, so both read it here.</para>
///
/// <para>Its own type rather than a member on <see cref="LongPath"/>, which is about a path's
/// <em>length</em> and the <c>\\?\</c> prefix §6.3 requires. This is about a path's position in a
/// volume, which is a different question (G1).</para>
/// </summary>
public static class VolumeRoot
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Whether <paramref name="path"/> is the top of a drive in display form, <c>C:\</c>, and nothing
    /// else: not <c>C:</c>, which is drive-relative and means a different directory in every process;
    /// not <c>\?\C:\</c>, which the shell and Windows' Disk Cleanup handlers refuse; not a share, and
    /// not a folder a volume is mounted at.
    ///
    /// <para>For a call that hands Windows a whole volume and lets it decide what goes, which is where
    /// the value that crosses decides what is destroyed. Both such calls ask it here, so the two
    /// cannot come to disagree about what the top of a drive is.</para>
    /// </summary>
    public static bool IsDriveTop(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where <paramref name="path"/> sits below the top of the volume it is on, or null where it
    /// has no top to sit below — which is a relative path, a drive-relative one such as
    /// <c>C:file</c>, and a volume root itself, including a folder a volume is mounted at.
    ///
    /// <para>A root is answered as null rather than as an empty remainder, so a caller cannot
    /// mistake "the volume itself" for "something at the top of the volume". Those are the two
    /// answers the callers here have to keep apart: one is never a thing to remove and never a thing
    /// to explain, and the other is both.</para>
    ///
    /// <para>For a caller that explains. A caller that refuses reads <see cref="Readings"/>, which
    /// also keeps the drive letter's answer.</para>
    /// </summary>
    /// <param name="volumes">Asked where the volume holding the path is mounted.</param>
    /// <param name="path">
    /// A fully qualified path, ordinarily one <see cref="LongPath.Configured(string?)"/> has already
    /// normalised. Anything else answers null, because working a remainder out from a path that is
    /// not anchored anywhere would be inventing where it is.
    ///
    /// <para>Checked here rather than assumed, and that is a contract on a public type rather than
    /// the re-validation G3 bans. <c>C:pagefile.sys</c> is the shape that makes it worth stating: it
    /// is drive-relative rather than qualified, and its root and its remainder are indistinguishable
    /// from a path that really is at a volume root.</para>
    ///
    /// <para>The extended-length form is accepted and answered as its display form (§6.3), because
    /// that is the form the machine's answer comes back in.</para>
    /// </param>
    public static string? Below(IVolumeInventory volumes, string path) => Readings(volumes, path)?[0];

    /// <summary>
    /// Every position <paramref name="path"/> can be read at: below the folder its volume is
    /// mounted at first, where that is deeper than the path's own root, and below that root. Null
    /// where either reading makes the path a volume root, and for a path <see cref="Below"/> answers
    /// null for.
    ///
    /// <para><b>Both, for a caller that refuses.</b> Asking the machine is what finds the top of a
    /// volume mounted at a folder, and it must not also take away what the drive letter already
    /// showed. A volume mounted at a folder inside <c>C:\$Recycle.Bin</c> would otherwise read
    /// everything under it as ordinary, where the drive letter reads it as inside the Recycle Bin.
    /// A refusal that holds on either reading therefore holds, and the lookup can only add
    /// refusals to what the path's text alone gave.</para>
    /// </summary>
    public static IReadOnlyList<string>? Readings(IVolumeInventory volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var display = LongPath.Display(path);

        if (Path.GetPathRoot(display) is not { Length: > 0 } root
            || Remainder(display, root) is not { } belowRoot)
        {
            return null;
        }

        if (volumes.MountPointOf(display) is not { } mountPoint
            || mountPoint.Length <= root.Length
            || !HostVolume.Holds(mountPoint, display))
        {
            return [belowRoot];
        }

        return Remainder(display, mountPoint) is { } belowMountPoint ? [belowMountPoint, belowRoot] : null;
    }

    /// <summary>
    /// What follows <paramref name="top"/> in <paramref name="path"/>, which it is a prefix of, or
    /// null where nothing does. A mount point named without its trailing separator is as long as
    /// the path it names, which is the same directory, so it answers null as well.
    /// </summary>
    private static string? Remainder(string path, string top)
    {
        var below = top.Length < path.Length ? path[top.Length..].TrimStart(Separators) : string.Empty;

        return below.Length > 0 ? below : null;
    }
}
