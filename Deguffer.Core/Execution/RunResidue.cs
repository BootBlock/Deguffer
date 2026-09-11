using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What one run's own removals tried to take and left standing, gathered as the run goes.
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
