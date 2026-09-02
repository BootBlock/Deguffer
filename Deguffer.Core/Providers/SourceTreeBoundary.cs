using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where a search through a source folder stops, stated once for both routes that perform one.
///
/// <see cref="SourceDirectoryDiscovery"/> walks a root and enforces these limits by not descending.
/// The volume index has no traversal to stop — it already knows every directory on the volume — so
/// it has to apply the same limits as a filter over what it returns. Two enforcements of one rule
/// is the price of having two routes; keeping the rule itself here is what stops them becoming two
/// different answers to the same question, chosen by whether the user happened to elevate.
///
/// The walk's third limit — that it never enters a reparse point — is not repeated here. The index
/// applies that one where it can see it, in the table, and never offers a path that passes through
/// a link at all.
/// </summary>
internal static class SourceTreeBoundary
{
    /// <summary>
    /// Directories a search never enters <em>unless it is looking for them</em>. <c>node_modules</c>
    /// is the expensive one — hundreds of thousands of entries, in a tree that holds no .NET
    /// intermediate output at all.
    ///
    /// <para>A name being sought overrides this, and the two rules do not conflict: a search stops
    /// at a directory it is looking for, without descending, because everything below a candidate
    /// belongs to that candidate. So a search for <c>node_modules</c> pays for one directory entry
    /// rather than for the tree beneath it — the same cost this boundary exists to avoid — while a
    /// search for <c>obj</c> still refuses to go in.</para>
    ///
    /// <para><c>.git</c> is here for a different reason and is never sought: a repository's object
    /// store holds directories of every conceivable name, none of which is build output, and the
    /// cost of a rule reaching inside one is not measured in disk space.</para>
    /// </summary>
    private static readonly FrozenSet<string> NeverEntered =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, ".git", "node_modules");

    public static bool IsNeverEntered(string name) => NeverEntered.Contains(name);

    /// <summary>
    /// Whether the walk would also have offered <paramref name="candidate"/>. The walk is the
    /// guaranteed route and the index is an accelerator, so where they can differ, the walk's answer
    /// is the one that defines the question.
    ///
    /// <paramref name="candidate"/> must already be known to sit at or below <paramref name="root"/>;
    /// the scanner narrows to the approved root before anything reaches here.
    /// </summary>
    /// <param name="names">
    /// Every name the pass is looking for. The whole set matters rather than the one name that
    /// matched: a directory below <em>any</em> candidate belongs to that candidate, so a
    /// <c>dist</c> inside a <c>node_modules</c> is out of reach exactly when <c>node_modules</c> is
    /// also being sought — which is the point at which the walk stops descending too.
    /// </param>
    public static bool WouldBeFoundByWalking(string candidate, string root, FrozenSet<string> names)
    {
        // Asked for rather than derived by slicing the candidate at the root's length: that
        // arithmetic is only correct while this and the scanner's own narrowing spell a path the
        // same way, and a mis-slice would quietly start offering directories outside the root.
        var relative = Path.GetRelativePath(LongPath.Display(root), LongPath.Display(candidate))
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // The root is where the search starts, so it is never one of the search's answers — it
        // answers "." here. The user approved that folder as a place to look inside, which is not
        // the same as offering it up.
        if (relative.Length == 0 || (relative.Length == 1 && relative[0] == "."))
        {
            return false;
        }

        // Everything above the candidate. Anything below a directory the walk refuses to enter was
        // never findable, and anything below another candidate belongs to that candidate — offering
        // it as well would make two steps where one deletes the other's parent.
        for (var i = 0; i < relative.Length - 1; i++)
        {
            if (names.Contains(relative[i]) || IsNeverEntered(relative[i]))
            {
                return false;
            }
        }

        return true;
    }
}
