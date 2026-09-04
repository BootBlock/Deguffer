namespace Deguffer.Core.Safety;

/// <summary>
/// Where a path sits relative to the top of its own volume.
///
/// <para>Read from the path rather than from a list of drives, and that is the whole point of it.
/// A table built from <see cref="IVolumeInventory"/> is a snapshot, so a volume mounted after it
/// was built would answer wrongly. The two callers are the one that refuses to delete a volume's
/// paging file and the one that explains to the reader what a paging file is, and both have to be
/// right on a drive plugged in a moment ago.</para>
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
    /// volume root itself.
    ///
    /// <para>A root is answered as null rather than as an empty remainder, so a caller cannot
    /// mistake "the volume itself" for "something at the top of the volume". Those are the two
    /// answers the callers here have to keep apart: one is never a thing to remove and never a thing
    /// to explain, and the other is both.</para>
    /// </summary>
    /// <param name="path">
    /// A fully qualified path, ordinarily one <see cref="LongPath.Configured(string?)"/> has already
    /// normalised. Anything else answers null, because working a remainder out from a path that is
    /// not anchored anywhere would be inventing where it is.
    /// </param>
    public static string? Below(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: > 0 } root)
        {
            return null;
        }

        var below = path[root.Length..].TrimStart(Separators);

        return below.Length > 0 ? below : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a direct child of the root of the volume it is on — the
    /// question <c>C:\pagefile.sys</c> answers yes to and <c>C:\Windows\System32</c> answers no to.
    /// </summary>
    public static bool Holds(string path) =>
        Below(path) is { } below && below.IndexOfAny(Separators) < 0;
}
