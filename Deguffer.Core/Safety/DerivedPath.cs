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
    /// is, and null too where the walk stopped for any other reason.
    ///
    /// <para>Every segment, not just the last. A junction partway down redirects the deletion
    /// exactly as effectively as one at the target, and is rather more likely: relocating a cache
    /// onto another volume is a thing people do on purpose.</para>
    ///
    /// <para><paramref name="target"/> must sit under <paramref name="baseDirectory"/>, which every
    /// caller satisfies by construction — the base is the root the target was assembled from.</para>
    /// </summary>
    public static string? FirstLinkBetween(string baseDirectory, string target) =>
        FirstObstacleBetween(baseDirectory, target) is { IsLink: true } obstacle ? obstacle.Path : null;

    /// <summary>
    /// The first segment of the derived path that stops the walk, and what stopped it — or null
    /// where nothing did.
    ///
    /// <para><b>The two obstacles are told apart rather than folded together, and that is the whole
    /// reason this exists beside <see cref="FirstLinkBetween"/>.</b> A link is a fact about the
    /// machine, and every caller renders it as one: "it is a link to somewhere else". Windows
    /// refusing to describe a segment is not that fact, and saying it would be a specific claim
    /// where the truth is that Deguffer could not tell. See <see cref="LongPath.IsReparsePoint"/>,
    /// which fails closed and must never be rendered.</para>
    ///
    /// <para>A segment that is simply <em>not there</em> stops the walk and is reported as nothing:
    /// a directory that does not exist is not a link and hides nothing, and the caller's own probe
    /// for the target says what its absence means.</para>
    /// </summary>
    public static DerivedPathObstacle? FirstObstacleBetween(string baseDirectory, string target)
    {
        var walked = baseDirectory;

        foreach (var segment in target[baseDirectory.Length..]
                     .Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);

            // One attribute read answers both questions this walk asks of a segment. The link
            // answer is read only on the Present arm below, which is the condition LongPath states
            // for it.
            switch (LongPath.ProbeDirectory(walked, out var link))
            {
                case PathPresence.Refused:
                    return new DerivedPathObstacle(walked, IsLink: false);

                case PathPresence.Absent:
                    return null;
            }

            if (link)
            {
                return new DerivedPathObstacle(walked, IsLink: true);
            }
        }

        return null;
    }
}

/// <summary>What stopped a walk down a derived path, and where.</summary>
/// <param name="Path">The segment the walk stopped at.</param>
/// <param name="IsLink">
/// True where that segment is a link, which is a fact a caller may state. False where Windows would
/// not describe it, which is not.
/// </param>
public readonly record struct DerivedPathObstacle(string Path, bool IsLink);
