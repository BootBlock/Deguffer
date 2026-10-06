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
/// <para><b>A solution a program works beside adds the projects it names.</b> Visual Studio works in
/// the solution's folder, and the projects it has open are below that folder rather than above it,
/// so walking up from where the editor is would never reach them. The solution names them, and each
/// named project's build directory is a candidate as well.</para>
///
/// <para><b>Each is judged exactly as the plan judges it.</b> The same boundary as discovery, the
/// provider's own recogniser, and <see cref="LiveTreeVeto.Apply"/> with the question the plan
/// asks. Lying below a project is not the verdict: a program started from a project's own
/// <c>tools</c> folder, working somewhere else, is using neither the project nor its build output,
/// and the plan offers that build output. Declaring it here would refuse what the Storage page
/// allows.</para>
///
/// <para><b>A candidate Windows would not describe is declared as well.</b> It can be neither
/// recognised nor checked for use, and a program is working beside it, so Explore refuses it rather
/// than allowing what nothing could judge. See <see cref="PathPresence"/>.</para>
///
/// <para><b>What this does not find.</b> A directory whose only evidence is a lock file held open by
/// a program that neither runs from inside the directory nor works under its project — a Unity
/// editor holding <c>UnityLockfile</c> with its working directory elsewhere — is live to the plan
/// and not declared here, because asking about a lock file means naming its directory first, which
/// is the walk. Nor does it find a project a solution names where the solution could not be read,
/// because there is nothing to name it by. The plan still holds both back.</para>
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
    /// <param name="questions">
    /// The rule that builds what the plan asks the veto, whose
    /// <see cref="LiveTreeQuestion.NamedProjects"/> are candidates as well.
    /// </param>
    /// <param name="volumes">Asked every other path a root and a place a program is are reachable at.</param>
    public static IReadOnlyList<ToolRoot> Declare(
        ILiveTreeInspector inspector,
        SourceDirectoryDiscovery discovery,
        IReadOnlyList<SourceRoot> roots,
        IReadOnlyList<string> names,
        Func<string, string?> recognise,
        Func<CancellationToken, LiveTreeQuestion> questions,
        IVolumeInventory volumes,
        CancellationToken ct)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        // Resolved first, because the process table holds whatever form a program was started with:
        // a path with '..' in it would otherwise be followed to somewhere it is not.
        var occupied = inspector.FindOccupiedDirectories(ct).Live
            .Select(place => LongPath.Configured(place.Directory))
            .OfType<string>()
            .Select(directory => ReachedFolder.At(directory, volumes))
            .ToList();

        // Built once, so the projects it names and the question the veto asks come from one reading.
        var question = questions(ct);
        var named = question.NamedProjects.Select(project => ReachedFolder.At(project, volumes)).ToList();

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

            var folder = ReachedFolder.At(root.Path, volumes);

            // Asked of the name and the boundary before the disk, because most places a program is
            // are nowhere near a build directory and a string answers that for free.
            candidates.UnionWith(discovery.WithinTheSearch(
                [
                    .. Candidates(root.Path, folder, occupied, names, ct),

                    // Only those below this root, because the boundary is asked of a candidate
                    // already known to be inside it, and a named project may be anywhere.
                    .. named
                        .Select(project => folder.Naming(project, root.Path))
                        .OfType<string>()
                        .SelectMany(project => names.Select(name => Path.Combine(project, name))),
                ],
                root.Path));
        }

        var recognised = new List<RecognisedBuildDirectory>();
        var unreached = new List<string>();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Existence is asked here because discovery only ever returns what is on disk, and a
            // recogniser that reads siblings alone would otherwise accept a directory that is not
            // there. The recogniser refuses a link at the directory or at its project, as it does for
            // the plan. A link further up is followed here where the plan's walk stops at it, which
            // refuses a directory the plan never reaches rather than allowing one it holds back.
            switch (LongPath.ProbeDirectory(candidate))
            {
                // Neither recognised nor ruled out, and a program is working beside it, so whether it
                // is in use is a question nothing could answer. Read as absent, Explore allowed it.
                case PathPresence.Refused:
                    unreached.Add(candidate);
                    break;

                case PathPresence.Present when recognise(candidate) is { } project:
                    recognised.Add(new RecognisedBuildDirectory(candidate, project));
                    break;
            }
        }

        var live = LiveTreeVeto.Apply(inspector, recognised, question, questions, ct);

        return
        [
            .. live.Vetoed.Select(vetoed => new ToolRoot(vetoed.Directory, Reason(vetoed), static _ => false)),
            .. unreached.Select(candidate => new ToolRoot(candidate, UnreachedReason, static _ => false)),
        ];
    }

    /// <summary>Why Explore refuses a candidate Windows would not describe.</summary>
    private const string UnreachedReason =
        "Windows would not say what is here, so Deguffer could not tell whether the program running "
        + "beside it is using it. It can go once Windows will describe it, or once that program has closed.";

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
    /// <param name="folder"><paramref name="root"/>, at every path it is reachable at.</param>
    private static List<string> Candidates(
        string root,
        ReachedFolder folder,
        IReadOnlyList<ReachedFolder> occupied,
        IReadOnlyList<string> names,
        CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        foreach (var place in occupied)
        {
            ct.ThrowIfCancellationRequested();

            if (folder.Naming(place, root) is not { } directory)
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
