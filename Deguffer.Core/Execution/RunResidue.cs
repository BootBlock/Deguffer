using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What one run's own removals tried to take and left standing, and which spared entries they left
/// alone, gathered as the run goes.
///
/// <para><b>§5.6 needs it because a refusal is evidence in itself.</b> Windows will not remove a
/// folder a program is working in, and removal tries a folder only after every folder inside it, so a
/// removal that reaches into a protected folder and meets one takes every file around it and leaves
/// that folder standing, with every folder above it. Asked afterwards, the protected folder still
/// holds a folder and reads as a survivor. One refused file does the same to any question about
/// content, because the folder still holds the file. What neither shape can hide is that a removal was inside the folder at all, and
/// what it could not take is where it says so. See <see cref="PlanVerifier"/> for how that is
/// read.</para>
///
/// <para><b>Only what a removal left strictly below its own root.</b> A removal's root is the step's
/// own subject: a scratch folder is kept on purpose, a folder something is using is reported by the
/// step, and a protected folder at or above the root holds the target rather than sitting inside
/// it. So each directory left standing is recorded with every folder between it and the root, and
/// the root never is. A protected folder in that set is one a removal rooted above it went
/// into.</para>
///
/// <para><b>Separate from <see cref="RunReach"/>, because it is what happened rather than what may
/// happen.</b> The reach is fixed before the first deletion. This grows with every removal, so a plan
/// verified later in the run sees what an earlier plan's removal left inside its folders. A plan
/// verified earlier cannot see what a later one leaves, which is as true of a folder a later plan
/// deletes outright.</para>
///
/// <para><b>What a removal left alone is the other half of the same evidence.</b> A spared entry is
/// one a program is using, and a program often removes its own scratch folder when it finishes. The
/// folder holding it is still standing, so the absence alone reads as a removal that failed to spare
/// it. What a removal can show it never acted on is recorded against that removal's root, and
/// <see cref="PlanVerifier"/> asks it of every removal whose root holds the missing path. See
/// <see cref="RemovalOutcome.LeftAlone"/>.</para>
///
/// <para><b>What had already gone when the run began is the evidence a removal cannot give.</b> A
/// walk records only what it met, so an entry its program removed between the preview and the clean
/// is not among what the removal left alone, and its absence reads as the removal's doing. Saving an
/// unsaved Arduino sketch deletes the temporary folder the plan keeps, so the ordinary case is a user
/// who saves the sketch and then presses Clean. <see cref="Begin"/> reads every path the run will be
/// held to before the first deletion, and a path absent then is one no removal in the run can have
/// taken, under any spelling or through any link.</para>
///
/// <para><b>Safe for concurrent use.</b> The steps of a plan that destroy unrelated folders run at
/// once, and each records its own removal as it ends. What the removals record is read and written
/// under one lock, which costs nothing a removal would notice: each records once a step, and §5.6 reads
/// once the steps are over. What had gone before the run is written only by <see cref="Begin"/>, before
/// anything else can see the record.</para>
/// </summary>
public sealed class RunResidue
{
    private readonly Lock _gate = new();

    /// <summary>
    /// Every folder recorded, kept per removal root. Each set is closed upwards as far as its own
    /// root and no further, which is what lets a record stop climbing at the first folder its set
    /// already holds. One set for every root would break that: a folder a narrower removal recorded
    /// would stop a broader one climbing past the narrower root, and the folders between the two
    /// roots would never be recorded.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The spared entries each removal left alone, one set per removal and kept per root, all in the
    /// one form compared here.
    ///
    /// <para>One set per removal rather than one per root, because two removals of one folder answer
    /// separately. Merged, a removal that found an entry already gone, because an earlier removal of
    /// the same folder took it, would vouch for the removal that did.</para>
    /// </summary>
    private readonly Dictionary<string, List<HashSet<string>>> _leftAloneByRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every path the run is held to that Windows said was not there before the first deletion, in the
    /// one form compared here. Empty for a record made without <see cref="Begin"/>, which knows of
    /// nothing gone and so excuses nothing.
    /// </summary>
    private readonly HashSet<string> _goneBeforeRun = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A record that read nothing of the disk before the run, so it can say only what the run's own
    /// removals recorded. A run reads the disk first through <see cref="Begin"/>.
    /// </summary>
    public RunResidue()
    {
    }

    /// <summary>
    /// The record for a run of <paramref name="plans"/>, made before the first of them deletes anything,
    /// holding each path they will be held to that has already gone.
    ///
    /// <para><b>Absent, not merely undescribed.</b> A path Windows will not describe may be there, and
    /// reading it as gone would excuse its loss on nothing.</para>
    ///
    /// <para><b>What it cannot see</b> is an entry that comes back during the run and is then taken by
    /// it: it was gone when the run began, so its loss reads as somebody else's. A removal walk that
    /// meets a kept entry standing holds it back by its path, so only an over-reach somewhere else in
    /// the run could take it. Nor can it tell a stranger from an earlier clean of Deguffer's own: a
    /// path gone before this run began reads as removed from outside it whoever removed it, as a path
    /// whose whole folder had gone always has.</para>
    /// </summary>
    public static RunResidue Begin(IReadOnlyList<CleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var residue = new RunResidue();

        foreach (var path in plans.SelectMany(PathsHeldStanding))
        {
            if (LongPath.ProbeEntry(path) is PathPresence.Absent)
            {
                residue._goneBeforeRun.Add(Normalised(path));
            }
        }

        return residue;
    }

    /// <summary>
    /// Whether <paramref name="path"/> had already gone when the run began, so nothing in the run can
    /// have removed it. False for a path <see cref="Begin"/> was not asked about.
    /// </summary>
    public bool WasGoneBeforeRun(string path) => _goneBeforeRun.Contains(Normalised(path));

    /// <summary>
    /// Every path whose absence <see cref="PlanVerifier.Verify"/> judges in <paramref name="plan"/>: each
    /// protected path that was there to preserve, and each file a <see cref="ReleaseLocalCopiesStep"/>
    /// named. A path the verifier asks about that is missing here is one whose loss stays the alarm.
    /// </summary>
    private static IEnumerable<string> PathsHeldStanding(CleanupPlan plan) =>
        plan.ProtectedPaths
            .Where(protectedPath => protectedPath.PresenceBefore is not PathPresence.Absent)
            .Select(protectedPath => protectedPath.Path)
            .Concat(plan.Steps.OfType<ReleaseLocalCopiesStep>().SelectMany(release => release.Files).Select(file => file.Path));

    /// <param name="root">The path the removal was handed, as its step named it.</param>
    /// <param name="leftStanding">
    /// What that removal could not take: <see cref="RemovalOutcome.LeftStanding"/>. It need not list
    /// the folders above each entry, because those are recorded here.
    /// </param>
    public void Record(string root, IReadOnlyList<string> leftStanding)
    {
        ArgumentNullException.ThrowIfNull(leftStanding);

        if (leftStanding.Count == 0)
        {
            return;
        }

        var top = Normalised(root);
        var folders = leftStanding.Select(Normalised).ToList();

        lock (_gate)
        {
            if (!_byRoot.TryGetValue(top, out var inside))
            {
                inside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _byRoot[top] = inside;
            }

            foreach (var standing in folders)
            {
                // Strictly below the root means the folder has a parent, so the climb always has
                // somewhere to go until it reaches the root and stops.
                for (var folder = standing; IsStrictlyBelow(top, folder) && inside.Add(folder);)
                {
                    folder = Path.GetDirectoryName(folder)!;
                }
            }
        }
    }

    /// <param name="root">The path the removal was handed, as its step named it.</param>
    /// <param name="leftAlone">
    /// What that removal can show it never acted on: <see cref="RemovalOutcome.LeftAlone"/>. Recorded
    /// when it is empty too, because an empty record is a removal that vouches for nothing, and it
    /// must stop any other removal of the same folder vouching on its behalf.
    /// </param>
    public void RecordLeftAlone(string root, IReadOnlyList<string> leftAlone)
    {
        ArgumentNullException.ThrowIfNull(leftAlone);

        var top = Normalised(root);
        var entries = new HashSet<string>(leftAlone.Select(Normalised), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            if (!_leftAloneByRoot.TryGetValue(top, out var removals))
            {
                removals = [];
                _leftAloneByRoot[top] = removals;
            }

            removals.Add(entries);
        }
    }

    /// <summary>
    /// Whether every removal handed <paramref name="root"/> left <paramref name="path"/> alone: it,
    /// or an entry holding it, is one each of them can show it never acted on.
    ///
    /// <para>An entry holding it counts, because a removal that never entered a folder took nothing
    /// inside it. A root no removal has recorded answers false, so a removal this run has not made
    /// yet is never read as one that left anything alone.</para>
    /// </summary>
    public bool LeftAlone(string root, string path)
    {
        var (top, key) = (Normalised(root), Normalised(path));

        lock (_gate)
        {
            return _leftAloneByRoot.TryGetValue(top, out var removals)
                && removals.TrueForAll(entries => entries.Any(entry => LongPath.Contains(entry, key)));
        }
    }

    /// <summary>
    /// The root of every removal that left something standing below it, in the one form compared here,
    /// so a caller can name a folder below each root as the root names itself before asking
    /// <see cref="Entered"/> about it.
    /// </summary>
    public IReadOnlyList<string> EnteredRoots
    {
        get
        {
            lock (_gate)
            {
                return [.. _byRoot.Keys];
            }
        }
    }

    /// <summary>
    /// Whether a removal rooted above <paramref name="folder"/> went into it, and left something
    /// standing at or below it. Asked of the folder as it is named here, and a removal records what it
    /// left as its root was named.
    /// </summary>
    public bool Entered(string folder)
    {
        var key = Normalised(folder);

        lock (_gate)
        {
            return _byRoot.Values.Any(inside => inside.Contains(key));
        }
    }

    private static bool IsStrictlyBelow(string root, string path) =>
        !path.Equals(root, StringComparison.OrdinalIgnoreCase) && LongPath.Contains(root, path);

    /// <summary>
    /// The one form every path here is compared in. The removal walks from
    /// <see cref="LongPath.Extended"/> of its root, which resolves the path before prefixing it, so a
    /// root is put through the same resolution rather than compared as its step spelled it. A
    /// difference in spelling alone would otherwise leave nothing below the root, and record nothing.
    /// </summary>
    private static string Normalised(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(LongPath.Extended(path)));
}
