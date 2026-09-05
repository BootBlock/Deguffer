namespace Deguffer.Core.Safety;

/// <summary>
/// The link check a path that was <em>built</em> needs, and an enumerated one does not.
///
/// <para>Almost every target in Deguffer arrives from <see cref="ChildDirectories.Under"/>, which
/// separates links out before a caller ever sees them — so one reparse check on the directory
/// itself completes the argument. A path assembled from an application-data root plus a few
/// constants has passed through no such filter, and a junction at any segment of it puts the
/// deletion on the far side while every §5.6 survivor named below resolves through the same link
/// and passes. That is the vacuous negative: a plan that proves nothing survived anywhere near
/// where it deleted.</para>
///
/// <para>Shared rather than written per provider because it is one rule, and because the segment a
/// copy forgets to check is invisible until somebody has relocated that directory. Firefox's local
/// profile root and the Epic launcher's <c>Saved</c> folder are both built this way, and both are
/// under directories people move onto another volume deliberately.</para>
/// </summary>
public static class DerivedPath
{
    private static readonly char[] Separators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// The first directory between <paramref name="baseDirectory"/> and <paramref name="target"/>,
    /// inclusive of the target, that is a link rather than a directory — or null when none of them
    /// is.
    ///
    /// <para>Every segment, not just the last. A junction partway down redirects the deletion
    /// exactly as effectively as one at the target, and is rather more likely: relocating a cache
    /// onto another volume is a thing people do on purpose.</para>
    ///
    /// <para><paramref name="target"/> must sit under <paramref name="baseDirectory"/>, which every
    /// caller satisfies by construction — the base is the root the target was assembled from.</para>
    /// </summary>
    public static string? FirstLinkBetween(string baseDirectory, string target)
    {
        var walked = baseDirectory;

        foreach (var segment in target[baseDirectory.Length..]
                     .Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);

            if (LongPath.IsReparsePoint(walked))
            {
                return walked;
            }
        }

        return null;
    }
}
