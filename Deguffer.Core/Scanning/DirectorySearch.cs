using System.Collections.Frozen;

namespace Deguffer.Core.Scanning;

/// <summary>What a search by name does at one directory below the root it started in.</summary>
public enum SearchStep
{
    /// <summary>The directory carries a sought name. It is an answer, and is not descended into.</summary>
    Found,

    /// <summary>The directory is neither sought nor entered, so nothing below it is an answer.</summary>
    Passed,

    /// <summary>The directory is descended into.</summary>
    Entered,
}

/// <summary>
/// A search for directories by name below one root, and where it stops: at a directory it finds,
/// because everything below a candidate belongs to that candidate, and at a directory it never
/// enters. The root itself is where the search starts, so it is never judged.
///
/// <para>One judgement for every route that searches. The walk applies it as it lists each
/// directory, and the volume index as it descends the table's child links, so the two cannot become
/// two rules chosen by whether the user happened to elevate. The names are compared as the sets
/// compare them, which for the source discovery is without regard to case.</para>
/// </summary>
/// <param name="sought">The names a directory is an answer for.</param>
/// <param name="neverEntered">The names a directory is not descended into unless it is sought.</param>
public sealed class DirectorySearch(FrozenSet<string> sought, FrozenSet<string> neverEntered)
{
    /// <summary>The <see cref="HandedOff"/> directories, each joined into one relative path.</summary>
    private FrozenSet<string> _handedOff = FrozenSet<string>.Empty;

    /// <summary>
    /// Directories below the root, as components below it, that this search does not descend into
    /// because a search of their own covers them. Empty unless <see cref="HandingOff"/> made this.
    ///
    /// <para>Each is still met by name, so one that carries a sought name is still an answer: only
    /// the descent is handed off, and the search of its own lists everything below it by the same
    /// rules. Matched as the routes spell the path, so one spelled in another case is entered as
    /// before, which costs a second listing rather than a folder nobody searched.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> HandedOff { get; private init; } = [];

    public SearchStep Meet(string name) =>
        sought.Contains(name) ? SearchStep.Found
        : neverEntered.Contains(name) ? SearchStep.Passed
        : SearchStep.Entered;

    /// <summary>
    /// This search, stopping at each of <paramref name="belowRoot"/> rather than descending into it.
    /// For approved roots that nest, whose inner root has a search of its own: without this, every
    /// directory in it is listed once by each.
    /// </summary>
    public DirectorySearch HandingOff(IReadOnlyList<IReadOnlyList<string>> belowRoot) =>
        belowRoot.Count == 0 ? this : new(sought, neverEntered)
        {
            HandedOff = belowRoot,
            _handedOff = belowRoot.Select(Joined).ToFrozenSet(StringComparer.Ordinal),
        };

    /// <summary>
    /// Whether a search descends as far as the directory at <paramref name="belowRoot"/>, given as
    /// components below the root: every directory above it entered, and none of them handed off.
    /// Whether the directory itself is an answer is <see cref="Meet"/>'s to say. The root is never
    /// reached, because it is where the search starts rather than one of its answers.
    /// </summary>
    public bool Reaches(IReadOnlyList<string> belowRoot)
    {
        if (belowRoot.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < belowRoot.Count - 1; i++)
        {
            if (Meet(belowRoot[i]) is not SearchStep.Entered
                || (_handedOff.Count > 0 && _handedOff.Contains(Joined(belowRoot.Take(i + 1)))))
            {
                return false;
            }
        }

        return true;
    }

    private static string Joined(IEnumerable<string> components) =>
        string.Join(Path.DirectorySeparatorChar, components);
}
