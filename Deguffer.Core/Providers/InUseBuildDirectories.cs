using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The build directories in the user's source folders that a plan holds back because something is
/// using them, declared so that Explore refuses them too (§7.1).
///
/// <para><b>Found from the process table rather than from the disk.</b> A plan finds them by walking
/// every approved root and asking which of what it found is in use. Explore cannot take that route:
/// its policy is rebuilt each time the page opens and each time a scan starts, and every path is
/// refused until it is ready, so a walk of the developer's source folders would hold the whole page
/// shut on each rebuild. The plan's rule gives a shorter way in. A directory is live to it where a
/// program runs from inside the directory or works anywhere in its project, so every such directory
/// is a child of a directory on the way down from an approved root to a place a program is — and
/// those few children are all that is looked at.</para>
///
/// <para><b>Each is judged exactly as the plan judges it.</b> The same boundary as discovery, the
/// provider's own recogniser, and <see cref="LiveTreeVeto.Apply"/> with the lock files the plan
/// passes. Lying below a project is not the verdict: a program started from a project's own
/// <c>tools</c> folder, working somewhere else, is using neither the project nor its build output,
/// and the plan offers that build output. Declaring it here would refuse what the Storage page
/// allows.</para>
///
/// <para><b>What this does not find.</b> A directory whose only evidence is a lock file held open by
/// a program that neither runs from inside the directory nor works under its project — a Unity
/// editor holding <c>UnityLockfile</c> with its working directory elsewhere — is live to the plan
/// and not declared here, because asking about a lock file means naming its directory first, which
/// is the walk. The plan still holds that directory back.</para>
/// </summary>
internal static class InUseBuildDirectories
{
    /// <param name="discovery">The provider's own discovery, whose boundary its plan applies.</param>
    /// <param name="roots">
    /// The approved roots. Empty declares nothing, as it plans nothing, and a root the plan would
    /// refuse to search declares nothing either — see <see cref="SourceDirectoryDiscovery.Searches"/>.
    /// </param>
    /// <param name="names">The directory names the provider seeks.</param>
    /// <param name="recognise">
    /// The provider's identification of a candidate, answering with its project folder or null. The
    /// same call its plan makes, so a directory the plan declines is never declared as in use.
    /// </param>
    /// <param name="lockFilesOf">The lock files the plan hands to the veto, for each directory.</param>
    public static IReadOnlyList<ToolRoot> Declare(
        ILiveTreeInspector inspector,
        SourceDirectoryDiscovery discovery,
        IReadOnlyList<SourceRoot> roots,
        IReadOnlyList<string> names,
        Func<string, string?> recognise,
        Func<RecognisedBuildDirectory, IReadOnlyList<string>> lockFilesOf,
        CancellationToken ct)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        var occupied = inspector.FindOccupiedDirectories(ct).Live;

        // A set, because approved roots may nest, and a directory below both would otherwise be
        // asked about twice and declared twice.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            // A root the plan will not search declares nothing from here either. Explore already
            // refuses the whole volume, so this takes away no protection — and declaring from a root
            // the plan refuses would be this route judging a folder by a rule the plan does not.
            if (!discovery.Searches(root))
            {
                continue;
            }

            // Asked of the name and the boundary before the disk, because most places a program is
            // are nowhere near a build directory and a string answers that for free.
            candidates.UnionWith(
                discovery.WithinTheSearch(Candidates(root.Path, occupied, names, ct), root.Path));
        }

        var recognised = new List<RecognisedBuildDirectory>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Existence is asked here because discovery only ever returns what is on disk, and a
            // recogniser that reads siblings alone would otherwise accept a directory that is not
            // there. The recogniser refuses a link at the directory or at its project, as it does for
            // the plan. A link further up is followed here where the plan's walk stops at it, which
            // refuses a directory the plan never reaches rather than allowing one it holds back.
            if (LongPath.DirectoryExists(candidate) && recognise(candidate) is { } project)
            {
                recognised.Add(new RecognisedBuildDirectory(candidate, project));
            }
        }

        var live = LiveTreeVeto.Apply(inspector, recognised, lockFilesOf, ct);

        return [.. live.Vetoed.Select(vetoed => new ToolRoot(vetoed.Directory, Reason(vetoed), static _ => false))];
    }

    /// <summary>
    /// Every directory called one of <paramref name="names"/> whose parent lies on the way down from
    /// <paramref name="root"/> to a place a program is, the root included. A place outside the root
    /// contributes nothing: the root is the user's consent, and a program elsewhere is no reason to
    /// look inside it.
    ///
    /// <para>Each ancestor is visited once however many programs sit below it, and a walk stops at
    /// the first one already visited, because everything between that one and the root has been
    /// visited too.</para>
    /// </summary>
    private static List<string> Candidates(
        string root,
        IReadOnlyList<LiveTree> occupied,
        IReadOnlyList<string> names,
        CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var place in occupied)
        {
            ct.ThrowIfCancellationRequested();

            // Resolved first, because the process table holds whatever form a program was started
            // with and the approved roots have been resolved (§6.3): a path with '..' in it, or an
            // extended-length prefix, would otherwise compare as lying outside the root.
            if (LongPath.Configured(place.Directory) is not { } directory || !LongPath.Contains(root, directory))
            {
                continue;
            }

            for (var ancestor = directory;
                 ancestor is not null && visited.Add(ancestor);
                 ancestor = Path.GetDirectoryName(ancestor))
            {
                foreach (var name in names)
                {
                    candidates.Add(Path.Combine(ancestor, name));
                }

                if (ancestor.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        return candidates;
    }

    /// <summary>Why Explore refuses it, naming what the user would have to close.</summary>
    private static string Reason(LiveTree vetoed) =>
        "A running program is using this right now"
        + (vetoed.Holders.Count > 0 ? $" ({string.Join("; ", vetoed.Holders)})" : string.Empty)
        + ". Removing it from under an editor, a build or a program that is running breaks the work "
        + "in progress, so it can go once that has finished.";
}
