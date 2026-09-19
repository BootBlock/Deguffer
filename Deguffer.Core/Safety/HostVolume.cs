namespace Deguffer.Core.Safety;

/// <summary>
/// Which of the machine's volumes holds a path, asked of <see cref="IVolumeInventory"/> rather than
/// worked out from a drive letter at each call site.
///
/// <para>Three places have to know whether a path is on a volume whose contents are not on this
/// machine (<see cref="LocalVolume.StoresContentRemotely"/>): the folder an Explore scan is pointed
/// at, the folder the user approves in Settings, and the discovery pass that searches the folders
/// they approved. The Explore page answered it by matching <see cref="Path.GetPathRoot(string)"/>
/// against its own picker list, which is a page-local arrangement — Core cannot reach it, and it
/// answers only for the volumes that page chose to offer. The rule therefore lived where two of its
/// three callers could not get at it, which is how an approved source folder on a cloud mount came
/// to be walked in full.</para>
///
/// <para>Its own type rather than a member on <see cref="VolumeRoot"/>. That one answers where a
/// path sits in its volume, and asks the machine at each question rather than reading the
/// remembered list, because it has to be right about a volume mounted a moment ago. This one exists
/// to read the list, because what the volume <em>is</em> can only be read from the volume
/// (G1).</para>
/// </summary>
public static class HostVolume
{
    /// <summary>
    /// The volume <paramref name="path"/> is stored on, or null where the inventory holds none that
    /// does.
    ///
    /// <para><b>Null means nothing was measured, never that the path is safe.</b> A UNC share, a
    /// volume addressed by its GUID rather than by any mount point, and an inventory that could not
    /// be read all arrive here as null, so a caller that refused on one would be refusing on no
    /// reading at all — and a caller that treats it as an all-clear is making the same mistake in
    /// the other direction. All three callers that decide anything read a property of the volume
    /// they got back instead.</para>
    ///
    /// <para><b>The longest mount point that holds the path wins.</b> A volume mounted at a folder
    /// sits inside another volume by construction, so <c>C:\Mount\work</c> is held by <c>C:\</c> and
    /// by <c>C:\Mount\</c> alike and only the longer of the two is the volume the bytes are on.
    /// Answering the shorter would be a confident reading of the wrong volume rather than the
    /// non-answer above: a cloud client mounted at a folder would be reported as the local disk
    /// holding its mount point, and would be searched.</para>
    ///
    /// <para>Compared through <see cref="LongPath.Contains"/>, which is the one comparison that gets
    /// a volume root right: <c>C:\</c> keeps its trailing separator where no other path does, and
    /// appending a second one builds a prefix nothing below it can match. The extended-length prefix
    /// comes off first (§6.3) — every path in Core may arrive as <c>\\?\C:\…</c>, and that form
    /// starts with none of the mount points the inventory reports.</para>
    /// </summary>
    /// <param name="path">
    /// A rooted path. An unrooted one matches no volume and answers null, which is the same non-answer
    /// as a share: there is nothing to read about a location that names no volume.
    /// </param>
    public static LocalVolume? For(IVolumeInventory volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var comparable = LongPath.Display(path);

        // A loop rather than a LINQ maximum, because LocalVolume is a struct: those forms answer a
        // default-constructed volume for "no match", and a caller reading StoresContentRemotely off
        // that would be reading the flags of a volume that does not exist.
        LocalVolume? held = null;
        var reach = 0;

        foreach (var volume in volumes.Volumes)
        {
            foreach (var mountPoint in volume.MountPoints)
            {
                // Length first, so the comparison is skipped for every mount point that could not
                // beat the one already held.
                if (mountPoint.Length > reach && Holds(mountPoint, comparable))
                {
                    held = volume;
                    reach = mountPoint.Length;
                }
            }
        }

        return held;
    }

    /// <summary>
    /// Whether <paramref name="mountPoint"/> is where <paramref name="path"/> is stored.
    ///
    /// <para>The second test is the mount point directory named without its trailing separator,
    /// which is the form <see cref="LongPath.Configured(string?)"/> produces and therefore the form
    /// a stored source root arrives in. <c>C:\Mount</c> and <c>C:\Mount\</c> are the same directory,
    /// but only the second is a prefix of itself — so without this the mount point itself would be
    /// answered as the volume it sits on, which is the one path where being wrong matters most.</para>
    ///
    /// <para>Internal because <see cref="VolumeRoot"/> asks the same question of the machine's live
    /// answer, and a comparison that decides what is a volume root must be written once.</para>
    /// </summary>
    internal static bool Holds(string mountPoint, string path) =>
        LongPath.Contains(mountPoint, path)
        || path.Equals(Path.TrimEndingDirectorySeparator(mountPoint), StringComparison.OrdinalIgnoreCase);
}
