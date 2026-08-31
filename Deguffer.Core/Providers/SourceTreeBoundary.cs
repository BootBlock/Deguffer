using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where a search through a source folder stops, stated once for both routes that perform one.
///
/// <see cref="ObjDirectoryDiscovery"/> walks a root and enforces these limits by not descending.
/// The volume index has no traversal to stop — it already knows every directory on the volume — so
/// it has to apply the same limits as a filter over what it returns. Two enforcements of one rule
/// is the price of having two routes; keeping the rule itself here is what stops them becoming two
/// different answers to the same question, chosen by whether the user happened to elevate.
/// </summary>
internal static class SourceTreeBoundary
{
    /// <summary>
    /// Directories that would slow the walk considerably and can never hold a recognised candidate
    /// beneath them. <c>node_modules</c> is the expensive one — hundreds of thousands of entries in
    /// a tree that has no .NET intermediate output in it.
    /// </summary>
    private static readonly string[] NeverEntered = [".git", "node_modules"];

    public static bool IsNeverEntered(string name) =>
        NeverEntered.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the walk would also have offered <paramref name="candidate"/>. The walk is the
    /// guaranteed route and the index is an accelerator, so where they can differ, the walk's answer
    /// is the one that defines the question.
    ///
    /// <paramref name="candidate"/> must already be known to sit at or below <paramref name="root"/>;
    /// the scanner narrows to the approved root before anything reaches here.
    /// </summary>
    public static bool WouldBeFoundByWalking(string candidate, string root, string name)
    {
        var normalised = LongPath.Display(root).TrimEnd(Path.DirectorySeparatorChar);
        var relative = LongPath.Display(candidate)[normalised.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // The root is where the search starts, so it is never one of the search's answers. The user
        // approved that folder as a place to look inside, which is not the same as offering it up.
        if (relative.Length == 0)
        {
            return false;
        }

        // Everything above the candidate. Anything below a directory the walk refuses to enter was
        // never findable, and anything below another candidate belongs to that candidate — offering
        // it as well would make two steps where one deletes the other's parent.
        for (var i = 0; i < relative.Length - 1; i++)
        {
            if (IsNeverEntered(relative[i]) || relative[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
