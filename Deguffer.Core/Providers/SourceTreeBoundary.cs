using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where a search through a source folder stops, stated once for both routes that perform one.
///
/// <para><see cref="SourceDirectoryDiscovery"/> walks a root and enforces these limits by not
/// descending. The volume index has no traversal to stop — it already knows every directory on the
/// volume — so it has to apply the same limits as a filter over what it returns. Two enforcements
/// of one rule is the price of having two routes; keeping the rule itself here is what stops them
/// becoming two different <em>rules</em>, chosen by whether the user happened to elevate.</para>
///
/// <para>The walk's third limit — that it never enters a reparse point — is not repeated here. The
/// index applies that one where it can see it, in the table, and never offers a path that passes
/// through a link at all.</para>
///
/// <para><b>The rules are what agree. The reach is not, and cannot be made to.</b> A search's reach
/// is a property of the token it runs under, and the two routes do not share one. The walk
/// enumerates, so a directory the account may not list yields nothing at all — <see
/// cref="Safety.ChildDirectories.Under"/> returns nothing rather than a partial view — and the whole
/// subtree below it is out of the walk's reach. The MFT reads file records, which no ACL guards, so
/// an elevated run offers candidates from inside that subtree that an unelevated one never finds.
/// Established against a real denied directory rather than reasoned about; see
/// <c>RouteAgreementTests</c>.</para>
///
/// <para>Closing that gap was considered and is not possible here. It would mean asking, per
/// candidate ancestor, whether this process can list it — a directory enumeration each, on the one
/// route that exists because enumeration is too slow (§5.5) — and the answer would come back under
/// the <em>elevated</em> token, so it would describe a walk that never happened rather than the
/// unelevated one it is meant to agree with.</para>
///
/// <para>What matters is that the difference is reach and nothing else. Every rule below applies to
/// an indexed candidate exactly as the walk applies it, by name, which no permission affects. A
/// candidate that gets through is still inside a root the user approved, still has to be recognised
/// by the project around it (§5.2), still faces the live-tree veto, and still appears in the preview
/// before anything can happen to it. An elevated run reaching more of the folder it was asked to
/// look inside is §5.5's intent, not a hole in it.</para>
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
    /// Whether <paramref name="candidate"/> lies inside the region this search covers.
    ///
    /// <para>Deliberately not named for what the walk would have found. It answers by name only, so
    /// it can say that a candidate is within the rules and cannot say that the walk could have got
    /// there — see the reach paragraphs above for why nothing here can.</para>
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
    public static bool IsInsideTheSearch(string candidate, string root, FrozenSet<string> names)
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
