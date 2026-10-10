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

    /// <summary>How many drive letters there are, which bounds how far substituted letters chain.</summary>
    private const int Letters = 26;

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
    /// <para>For a caller that explains. A caller that refuses reads <see cref="Places"/>, which
    /// also keeps the drive letter's answer and asks about every other path the item is at.</para>
    /// </summary>
    /// <param name="volumes">Asked where the volume holding the path is mounted.</param>
    /// <param name="path">
    /// A fully qualified path, ordinarily one <see cref="LongPath.Configured(string?)"/> or
    /// <see cref="LongPath.Entry(string?)"/> has already normalised. Anything else answers null, because working a remainder out from a path that is
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
    /// <para><b>Both, for a caller that refuses</b>, which reads them for each of the item's
    /// <see cref="Places"/> rather than for its one path. Asking the machine is what finds the top of a
    /// volume mounted at a folder, and it must not also take away what the drive letter already
    /// showed. A volume mounted at a folder inside <c>C:\$Recycle.Bin</c> would otherwise read
    /// everything under it as ordinary, where the drive letter reads it as inside the Recycle Bin.
    /// A refusal that holds on either reading therefore holds, and the lookup can only add
    /// refusals to what the path's text alone gave.</para>
    /// </summary>
    public static IReadOnlyList<string>? Readings(IVolumeInventory volumes, string path) =>
        Read(volumes, path, out _, out _);

    /// <summary>
    /// Every path the item at <paramref name="path"/> is reachable at, each with its
    /// <see cref="Readings"/>: <paramref name="path"/> itself first, then the same item below each
    /// other place its volume is mounted, then the same for the folder its drive letter stands for
    /// where <c>subst</c> made the letter. Null where <see cref="Readings"/> answers null for any of
    /// them, because the item is then a whole volume wherever it is reached.
    ///
    /// <para><b>For a caller that refuses, which has to ask every rule about every one of them.</b>
    /// A rule is written about a path's text, and the system volume mounted at <c>D:\SysMount\</c>
    /// as well as at <c>C:\</c> puts <c>C:\Windows</c> at <c>D:\SysMount\Windows</c> too. Asked
    /// about that text alone, the region table and every §5.2 tool root answered it as unclassified,
    /// and removing it removes the same folder. So a refusal that holds at any of these paths
    /// holds, as one that holds on either reading does.</para>
    ///
    /// <para>The other places are asked of the machine at each call, for the reason
    /// <see cref="IVolumeInventory.MountPointOf"/> is, and from the mount point the path's first
    /// reading is taken below. That is the path's own root where the machine's answer is not a
    /// prefix of the path, and a junction on that root's volume is reached at every other mount of
    /// the volume as surely as at this one.</para>
    ///
    /// <para><b>A substituted letter is followed to its folder.</b> Windows names no volume for a
    /// letter standing for <c>C:\Users\testuser</c>, so no mount point leads from it, and the
    /// profile's application data at <c>S:\AppData\Local</c> would read as unclassified.</para>
    /// </summary>
    public static IReadOnlyList<VolumePlace>? Places(IVolumeInventory volumes, string path)
    {
        List<VolumePlace> places = [];
        string? reached = path;

        // One step per drive letter at most, because a substituted letter may stand for a folder
        // reached through another, and two may stand for each other.
        for (var step = 0; step < Letters && reached is not null; step++)
        {
            if (Mounted(volumes, reached) is not { } mounted)
            {
                return null;
            }

            // A letter standing for a volume's top leads to places its mount points already named.
            foreach (var place in mounted)
            {
                if (!places.Exists(known => known.Path.Equals(place.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    places.Add(place);
                }
            }

            reached = Substituted(volumes, mounted[0].Path);

            // Letters standing for each other come back to a path already read, and reading it
            // again would ask the machine the same questions and add nothing.
            if (reached is not null && places.Exists(known => known.Path.Equals(reached, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        return places;
    }

    /// <summary>
    /// Where <paramref name="path"/> leads: the path it names once each drive letter <c>subst</c> made
    /// is followed to the folder it stands for, in display form, and the path itself in display form
    /// where its letter stands for no folder.
    ///
    /// <para><b>For a caller that asks what holds a path rather than where it sits.</b> Windows names
    /// no volume for <c>S:</c> standing for a folder on a cloud mount, so the letter is matched by no
    /// mount point that holds the folder, and the folder read through it is on no volume at all.
    /// <see cref="Places"/> follows the letter as one step of finding every path; this is that step
    /// alone, for a caller that needs only where the bytes are.</para>
    ///
    /// <para>One step per drive letter at most, as in <see cref="Places"/>. Letters that stand for each
    /// other lead nowhere Windows can open, so the path is answered as it was named.</para>
    /// </summary>
    public static string Followed(IVolumeInventory volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var named = LongPath.Display(path);
        var reached = named;

        for (var step = 0; step < Letters; step++)
        {
            if (Substituted(volumes, reached) is not { } folder)
            {
                return reached;
            }

            reached = folder;
        }

        return named;
    }

    /// <summary>
    /// The path <paramref name="display"/> names through the folder its drive letter stands for,
    /// or null where the letter stands for no folder.
    /// </summary>
    private static string? Substituted(IVolumeInventory volumes, string display) =>
        Path.GetPathRoot(display) is { Length: > 0 } root && volumes.SubstituteOf(root) is { } folder
            ? Path.Join(folder, Remainder(display, root))
            : null;

    /// <summary>
    /// <paramref name="path"/> itself and the same item below each other place its volume is
    /// mounted, or null where the path is a volume root. See <see cref="Places"/>.
    /// </summary>
    private static List<VolumePlace>? Mounted(IVolumeInventory volumes, string path)
    {
        if (Read(volumes, path, out var display, out var top) is not { } readings)
        {
            return null;
        }

        var below = readings[0];
        List<VolumePlace> places = [new(display, readings)];

        foreach (var mountPoint in volumes.MountPointsOf(top))
        {
            if (HostVolume.IsMountPoint(top, mountPoint))
            {
                continue;
            }

            var elsewhere = Path.Join(mountPoint, below);
            var root = Path.GetPathRoot(elsewhere.AsSpan()).ToString();

            // Both readings where this mount point is a folder, for the reason Readings keeps both:
            // the volume mounted inside another's Recycle Bin is in that bin read from the letter.
            places.Add(new(
                elsewhere,
                mountPoint.Length > root.Length && Remainder(elsewhere, root) is { } belowRoot
                    ? [below, belowRoot]
                    : [below]));
        }

        return places;
    }

    /// <summary>
    /// <see cref="Readings"/>, with the display form it read and the mount point its first reading
    /// is below, for <see cref="Places"/> to find the volume's other mount points from.
    /// </summary>
    private static IReadOnlyList<string>? Read(
        IVolumeInventory volumes, string path, out string display, out string top)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        display = top = string.Empty;

        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        display = LongPath.Display(path);

        if (Path.GetPathRoot(display) is not { Length: > 0 } root
            || Remainder(display, root) is not { } belowRoot)
        {
            return null;
        }

        if (volumes.MountPointOf(display) is not { } mountPoint
            || mountPoint.Length <= root.Length
            || !HostVolume.Holds(mountPoint, display))
        {
            top = root;
            return [belowRoot];
        }

        top = mountPoint;
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
