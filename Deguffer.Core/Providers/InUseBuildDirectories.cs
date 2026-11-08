using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The build directories in the user's source folders that a plan holds back, because something is
/// using them or because whether anything is could not be asked, declared so that Explore refuses
/// them too (§7.1).
///
/// <para><b>The plan's own search, and the places programs are.</b> The candidates are every
/// directory the plan's search finds, so each one the plan holds back is asked about here by the
/// question the plan asks it. A directory is live to the plan where a program runs from inside it or
/// works anywhere in its project, so the children of a directory on the way down from an approved
/// root to a place a program is are candidates as well, and so is each project a solution a program
/// works beside names. Those reach a directory past a link that the plan's search stops at, which
/// refuses a directory the plan never reaches rather than allowing one a program is using.</para>
///
/// <para><b>Searched where the scope can ask, and from the scan's tree where it listed the root.</b>
/// The policy is built again each time the page opens and each time a scan starts, and every path is
/// refused until it is ready, so a root a scan listed whole is read from the scan's tree rather than
/// walked beside it (<see cref="SourceDirectoryDiscovery"/>). Any other root the scope reaches is
/// searched on the disk, as the plan searches it: every root when the page opens, before any scan,
/// and a root the scan reached without listing it whole. A directory created after a scan is found
/// by the next one. A root the scope does not reach is not searched, and is refused whole: the scope
/// said nothing it covers is there, so this refuses nothing a caller meant to ask about, and it is
/// what holds if one asks anyway.</para>
///
/// <para><b>Process-table evidence alone would leave gaps.</b> Where the process table could not be
/// read, the places are few or none, and the plan holds back every build directory in the approved
/// roots. A project whose own lock file could not be asked about, such as a Unity project past
/// <c>MAX_PATH</c>, or whose lock file a program holds open while it works elsewhere, has no program
/// near it. A project a solution names, where the solution could not be read, is named by nothing.
/// The plan holds each of them back, so Explore refuses each of them.</para>
///
/// <para><b>Each is judged exactly as the plan judges it.</b> The same boundary as discovery, the
/// provider's own recogniser, and <see cref="LiveTreeVeto.Apply"/> with the question the plan
/// asks. Lying below a project is not the verdict: a program started from a project's own
/// <c>tools</c> folder, working somewhere else, is using neither the project nor its build output,
/// and the plan offers that build output. Declaring it here would refuse what the Storage page
/// allows.</para>
///
/// <para><b>A candidate Windows would not describe is declared as well.</b> It can be neither
/// recognised nor checked for use, so Explore refuses it rather than allowing what nothing could
/// judge. See <see cref="PathPresence"/>.</para>
/// </summary>
internal static class InUseBuildDirectories
{
    /// <param name="discovery">The provider's own discovery, whose boundary its plan applies.</param>
    /// <param name="roots">
    /// The approved roots. Empty declares nothing, as it plans nothing, and a root the plan would
    /// refuse to search declares nothing either — see <see cref="SourceDirectoryDiscovery.Searches"/>.
    /// </param>
    /// <param name="names">The directory names the provider seeks.</param>
    /// <param name="tuning">What the walks of each volume run with, which bounds how many candidates are judged at once.</param>
    /// <param name="recognise">
    /// The provider's identification of a candidate, answering with its project folder or null. The
    /// same call its plan makes, so a directory the plan declines is never declared as in use.
    /// </param>
    /// <param name="questions">
    /// The rule that builds what the plan asks the veto, whose
    /// <see cref="LiveTreeQuestion.NamedProjects"/> are candidates as well.
    /// </param>
    public static async Task<IReadOnlyList<ToolRoot>> DeclareAsync(
        ILiveTreeInspector inspector,
        SourceDirectoryDiscovery discovery,
        IReadOnlyList<SourceRoot> roots,
        IReadOnlyList<string> names,
        ScanTuner tuning,
        Func<string, string?> recognise,
        Func<CancellationToken, LiveTreeQuestion> questions,
        CancellationToken ct)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        // Resolved first, because the process table holds whatever form a program was started with:
        // a path with '..' in it would otherwise be followed to somewhere it is not. Each is followed
        // through the inspector, which keeps the answer for the planning pass every build provider shares.
        var occupied = inspector.FindOccupiedDirectories(ct).Live
            .Select(place => LongPath.Entry(place.Directory))
            .OfType<string>()
            .Select(inspector.Reach)
            .ToList();

        // Built once, so the projects it names and the question the veto asks come from one reading.
        var question = questions(ct);
        var named = question.NamedProjects.Select(inspector.Reach).ToList();

        var searched = await discovery.FindAsync(roots, ct).ConfigureAwait(false);

        // A set, because approved roots may nest, and because the search and the places programs are
        // find many of the same directories. Each would otherwise be asked about and declared twice.
        var candidates = new HashSet<string>(searched.Named(names), StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            // A root the plan will not search declares nothing from here either. Explore already
            // refuses the whole volume, so this takes away no protection — and declaring from a root
            // the plan refuses would be this route judging a folder by a rule the plan does not.
            if (!discovery.Searches(root))
            {
                continue;
            }

            var folder = inspector.Reach(root.Path);

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

        IReadOnlyList<string> judging = [.. candidates];
        var judged = await DiskBoundWork.EachAsync(judging, tuning, candidate => Judge(candidate, recognise), ct)
            .ConfigureAwait(false);

        var recognised = new List<RecognisedBuildDirectory>();
        var unreached = new List<string>();

        for (var index = 0; index < judging.Count; index++)
        {
            switch (judged[index])
            {
                // Neither recognised nor ruled out, so whether it is in use is a question nothing could
                // answer. Read as absent, Explore allowed it.
                case { Presence: PathPresence.Refused }:
                    unreached.Add(judging[index]);
                    break;

                case { Project: { } project }:
                    recognised.Add(new RecognisedBuildDirectory(judging[index], project));
                    break;
            }
        }

        var live = LiveTreeVeto.Apply(inspector, recognised, question, questions, ct);

        return
        [
            .. live.Vetoed.Select(vetoed => new ToolRoot(vetoed.Directory, Reason(vetoed), static _ => false)),
            .. live.Unchecked.Select(candidate => new ToolRoot(candidate, LiveTreeVeto.UncheckedReason, static _ => false)),
            .. unreached.Select(candidate => new ToolRoot(candidate, UnreachedReason, static _ => false)),
            .. searched.NotSearchedRoots.Select(root => new ToolRoot(root, NotSearchedReason, static _ => false)),
        ];
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is there, and the project it belongs to where it is.
    ///
    /// <para>Existence is asked here because discovery only ever returns what is on disk, and a
    /// recogniser that reads siblings alone would otherwise accept a directory that is not there. The
    /// recogniser refuses a link at the directory or at its project, as it does for the plan. A link
    /// further up is followed here where the plan's walk stops at it, which refuses a directory the
    /// plan never reaches rather than allowing one it holds back.</para>
    /// </summary>
    private static Judgement Judge(string candidate, Func<string, string?> recognise)
    {
        var presence = LongPath.ProbeDirectory(candidate);

        return new Judgement(presence, presence is PathPresence.Present ? recognise(candidate) : null);
    }

    /// <summary>What <see cref="Judge"/> found of one candidate. A project only where it is present and recognised.</summary>
    private readonly record struct Judgement(PathPresence Presence, string? Project);

    /// <summary>Why Explore refuses an approved root the search did not look in.</summary>
    private const string NotSearchedReason =
        "Deguffer did not look in here for build output a program is using, because nothing it was "
        + "asked about was in here. Scan or search a folder that holds this one to act on what is inside.";

    /// <summary>Why Explore refuses a candidate Windows would not describe.</summary>
    private const string UnreachedReason =
        "Windows would not say what is here, so Deguffer could not tell whether a running program is "
        + "using it. It can go once Windows will describe it.";

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
