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
/// <para>Its own type rather than a member on <see cref="VolumeRoot"/>. That one reads a path's
/// position from the path itself and deliberately consults no list of drives; this one exists to
/// consult the list, because what the volume <em>is</em> can only be read from the volume (G1).</para>
/// </summary>
public static class HostVolume
{
    /// <summary>
    /// The volume <paramref name="path"/> is stored on, or null where the inventory holds none that
    /// does.
    ///
    /// <para><b>Null means nothing was measured, never that the path is safe.</b> A UNC share, a
    /// volume mounted without a drive letter, and an inventory that could not be read all arrive
    /// here as null, so a caller that refused on one would be refusing on no reading at all — and a
    /// caller that treats it as an all-clear is making the same mistake in the other direction. Both
    /// callers that decide anything read a property of the volume they got back instead.</para>
    ///
    /// <para>Compared through <see cref="LongPath.Contains"/>, which is the one comparison that gets
    /// a volume root right: <c>C:\</c> keeps its trailing separator where no other path does, and
    /// appending a second one builds a prefix nothing below it can match. The extended-length prefix
    /// comes off first (§6.3) — every path in Core may arrive as <c>\\?\C:\…</c>, and that form
    /// starts with none of the roots the inventory reports.</para>
    /// </summary>
    /// <param name="path">
    /// A rooted path. An unrooted one matches no volume and answers null, which is the same non-answer
    /// as a share: there is nothing to read about a location that names no volume.
    /// </param>
    public static LocalVolume? For(IVolumeInventory volumes, string path)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var comparable = LongPath.Display(path);

        // A loop rather than FirstOrDefault, because LocalVolume is a struct: the LINQ form answers a
        // default-constructed volume for "no match", and a caller reading StoresContentRemotely off
        // that would be reading the flags of a volume that does not exist.
        foreach (var volume in volumes.Volumes)
        {
            if (LongPath.Contains(volume.RootPath, comparable))
            {
                return volume;
            }
        }

        return null;
    }
}
