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
/// names no position in it. Each of those falls back to <see cref="Path.GetPathRoot(string)"/>,
/// which is exactly the answer this type gave before it asked, so the lookup can only move the top
/// of a volume deeper into the path and never lose one the path's text shows.</para>
///
/// <para>Its callers are the policy that refuses to delete a volume's paging file or NTFS's own
/// records, the reference that explains to a reader what those are, and a provider that will not
/// declare a whole volume as a tool's folder. A safety rule written twice is one that gets changed
/// once, so all of them read it here.</para>
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
    /// Where <paramref name="path"/> sits below the top of its volume, or null where it has no top
    /// to sit below — which is a relative path, a drive-relative one such as <c>C:file</c>, and a
    /// volume root itself, including a folder a volume is mounted at.
    ///
    /// <para>A root is answered as null rather than as an empty remainder, so a caller cannot
    /// mistake "the volume itself" for "something at the top of the volume". Those are the two
    /// answers the callers here have to keep apart: one is never a thing to remove and never a thing
    /// to explain, and the other is both.</para>
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
    public static string? Below(IVolumeInventory volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        if (!Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var display = LongPath.Display(path);

        if (MountedAt(volumes, display) is not { } root)
        {
            return null;
        }

        var below = root.Length < display.Length ? display[root.Length..].TrimStart(Separators) : string.Empty;

        return below.Length > 0 ? below : null;
    }

    /// <summary>
    /// The top of the volume <paramref name="path"/> is on, as a prefix of the path: the machine's
    /// answer where it is one and reaches deeper than the path's own root, and that root otherwise.
    ///
    /// <para>Compared through <see cref="LongPath.Contains"/>, which gets a volume root right, and
    /// against the mount point named without its trailing separator as well: <c>C:\Mount</c> is the
    /// folder a volume is mounted at, and is the volume's root rather than a child of the disk it
    /// sits on.</para>
    /// </summary>
    private static string? MountedAt(IVolumeInventory volumes, string path)
    {
        var lexical = Path.GetPathRoot(path);

        if (lexical is not { Length: > 0 })
        {
            return null;
        }

        return volumes.MountPointOf(path) is { } mountPoint
            && mountPoint.Length > lexical.Length
            && (LongPath.Contains(mountPoint, path)
                || path.Equals(Path.TrimEndingDirectorySeparator(mountPoint), StringComparison.OrdinalIgnoreCase))
                ? mountPoint
                : lexical;
    }
}
