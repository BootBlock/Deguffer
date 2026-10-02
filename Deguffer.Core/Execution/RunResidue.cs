using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What one run's own removals tried to take and left standing, which spared entries they left
/// alone, and which protected paths had already gone before the first of them.
///
/// <para><b>§5.6 needs it because a refusal is evidence in itself.</b> Windows will not remove a
/// folder a program is working in, and removal goes deepest first, so a removal that reaches into a
/// protected folder and meets one takes every file around it and leaves that folder standing, with
/// every folder above it. Asked afterwards, the protected folder still holds a folder and reads as a
/// survivor. One refused file does the same to any question about content, because the folder still
/// holds the file. What neither shape can hide is that a removal was inside the folder at all, and
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
/// <para>Not safe for concurrent use. A run executes one step at a time, which makes that step's
/// executor the only writer.</para>
/// </summary>
public sealed class RunResidue
{
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

    /// <summary>The protected paths already gone before the run removed anything, in the one form compared here.</summary>
    private readonly HashSet<string> _goneBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A record for a run about to carry out <paramref name="plans"/>, holding each protected path
    /// that was there when the plan was made and is already gone. Taken before the run's first
    /// removal, so nothing in the run can have taken one of them, under any spelling a step used.
    ///
    /// <para><b>Why a path can be gone before anything is removed.</b> A plan is made when the user
    /// previews and carried out when they clean, and the program a spared entry was spared for often
    /// removes that entry itself when it finishes. Its folder is one the run empties around it, so the
    /// folder still stands, and that is the shape an over-broad removal leaves. Only the moment the
    /// path went tells the two apart, and before the first removal is the one moment that needs no
    /// comparison of paths to be trusted.</para>
    ///
    /// <para>Only a path Windows says is not there counts. One it would not describe proves
    /// nothing. What this cannot see is a path that reappears during the run and that a removal then
    /// takes, which would need the path's owner to put it back and a rule to over-reach at once.</para>
    /// </summary>
    public static RunResidue Before(IReadOnlyList<CleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var residue = new RunResidue();

        foreach (var path in plans.SelectMany(plan => plan.ProtectedPaths)
                     .Where(path => path.PresenceBefore is PathPresence.Present)
                     .Select(path => path.Path))
        {
            if (LongPath.ProbeEntry(path) is PathPresence.Absent)
            {
                residue._goneBefore.Add(Normalised(path));
            }
        }

        return residue;
    }

    /// <summary>Whether <paramref name="path"/> was protected, and already gone before the run removed anything.</summary>
    public bool GoneBeforeTheRun(string path) => _goneBefore.Contains(Normalised(path));

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

        if (!_byRoot.TryGetValue(top, out var inside))
        {
            inside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _byRoot[top] = inside;
        }

        foreach (var directory in leftStanding)
        {
            var folder = Normalised(directory);

            // Strictly below the root means the folder has a parent, so the climb always has
            // somewhere to go until it reaches the root and stops.
            while (IsStrictlyBelow(top, folder) && inside.Add(folder))
            {
                folder = Path.GetDirectoryName(folder)!;
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

        if (!_leftAloneByRoot.TryGetValue(top, out var removals))
        {
            removals = [];
            _leftAloneByRoot[top] = removals;
        }

        removals.Add(new HashSet<string>(leftAlone.Select(Normalised), StringComparer.OrdinalIgnoreCase));
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
        if (!_leftAloneByRoot.TryGetValue(Normalised(root), out var removals))
        {
            return false;
        }

        var key = Normalised(path);

        return removals.TrueForAll(entries => entries.Any(entry => LongPath.Contains(entry, key)));
    }

    /// <summary>
    /// Whether a removal rooted above <paramref name="folder"/> went into it, and left something
    /// standing at or below it.
    /// </summary>
    public bool Entered(string folder)
    {
        var key = Normalised(folder);

        return _byRoot.Values.Any(inside => inside.Contains(key));
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
