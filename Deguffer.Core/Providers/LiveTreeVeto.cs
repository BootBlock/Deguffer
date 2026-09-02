using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One directory whose identity is established, and the project folder it belongs to.</summary>
/// <param name="Path">The directory a plan would remove.</param>
/// <param name="Project">Its project or solution folder, which must survive (§5.6).</param>
public readonly record struct RecognisedBuildDirectory(string Path, string Project);

/// <param name="Cleared">The directories a plan may go on to target.</param>
/// <param name="Vetoed">The directories something is using, and what is using each.</param>
/// <param name="Complete">
/// False where liveness could not be established at all, so <see cref="Cleared"/> holds directories
/// nothing has vouched for.
/// </param>
public sealed record LiveTreeVetoResult(
    IReadOnlyList<RecognisedBuildDirectory> Cleared,
    IReadOnlyList<LiveTree> Vetoed,
    bool Complete);

/// <summary>
/// §5.3 applied to a source tree: a directory something is using is never a target.
///
/// <para>Written once for every provider that walks source roots, because it is a safety rule rather
/// than a shape. Six copies of "ask, then partition" would be six chances for one of them to warn
/// where it should refuse — and the difference between those two is the whole reason this exists.
/// A cache a tool is still writing to costs a slower next use. A build directory removed under a
/// live editor breaks the work in progress.</para>
/// </summary>
internal static class LiveTreeVeto
{
    /// <summary>Why a vetoed directory is listed as a survivor, in the §5.6 report.</summary>
    public const string ProtectedReason = "Something is using this project right now, so it is left alone.";

    public static LiveTreeVetoResult Apply(
        ILiveTreeInspector inspector,
        IReadOnlyList<RecognisedBuildDirectory> candidates,
        IReadOnlyList<string> lockFiles,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return new LiveTreeVetoResult([], [], Complete: true);
        }

        var findings = inspector.FindLive(
            [.. candidates.Select(c => new LiveTreeQuery(c.Path, c.Project, lockFiles))],
            ct);

        return new LiveTreeVetoResult(
            [.. candidates.Where(c => !findings.IsLive(c.Path))],
            findings.Live,
            findings.Complete);
    }

    /// <summary>
    /// What the user is told about the directories that were held back, or null if none were.
    ///
    /// A warning rather than information: the plan is smaller than the disk suggests, and the reason
    /// is something the user can act on by closing the editor. Saying nothing would leave a project
    /// silently missing from a list it belongs in.
    /// </summary>
    public static PlanNote? NoteFor(IReadOnlyList<LiveTree> vetoed)
    {
        if (vetoed.Count == 0)
        {
            return null;
        }

        // Each project with its own holders, rather than one project's holders attributed to all of
        // them. This is the sentence the user acts on to decide what to close, so naming the wrong
        // process on it is worse than naming none: it sends them to shut down something innocent and
        // leaves them believing the check misfired when the project stays on the list.
        var held = vetoed.Select(v => $"{Name(v.Directory)} in {Name(Parent(v.Directory))} ({string.Join("; ", v.Holders)})");

        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"Left {string.Join(", ", held)} alone. " +
            "Close what is using each one and preview again to include it.");
    }

    private static string Name(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    private static string Parent(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

    /// <summary>
    /// The note for a check that could not run, or null where it could.
    ///
    /// §5.5 makes a measurement fallback observable for the same reason this is said out loud: a
    /// safeguard that could not run must not look like a safeguard that found nothing.
    /// </summary>
    public static PlanNote? IncompleteNote(bool complete) => complete
        ? null
        : new PlanNote(
            PlanNoteSeverity.Warning,
            "Deguffer could not check whether these projects are in use. Close any editor or build " +
            "before cleaning them.");
}
