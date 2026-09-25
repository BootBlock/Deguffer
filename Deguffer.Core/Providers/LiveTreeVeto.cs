using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One directory whose identity is established, and the project folder it belongs to.</summary>
/// <param name="Path">The directory a plan would remove.</param>
/// <param name="Project">Its project or solution folder, which must survive (§5.6).</param>
public readonly record struct RecognisedBuildDirectory(string Path, string Project);

/// <summary>A directory the veto found nothing using, and the veto's question to ask again at the clean.</summary>
/// <param name="Path">
/// The directory a plan may go on to target, or, for a Squirrel installation, the folder holding the
/// builds it targets.
/// </param>
/// <param name="Project">Its project or solution folder, which must survive (§5.6).</param>
/// <param name="StillUnused">
/// What every step removing this directory, or anything the veto's answer about it cleared, carries as
/// its <see cref="DeleteStep.UseCheck"/>. Handed out with the directory rather than built by each
/// provider, so a directory cannot be offered on the veto's answer without its question.
/// </param>
public sealed record ClearedBuildDirectory(string Path, string Project, IUseCheck StillUnused);

/// <param name="Cleared">The directories a plan may go on to target.</param>
/// <param name="Vetoed">The directories something is using, and what is using each.</param>
/// <param name="Complete">
/// False where liveness could not be established at all, so <see cref="Cleared"/> holds directories
/// nothing has vouched for.
/// </param>
public sealed record LiveTreeVetoResult(
    IReadOnlyList<ClearedBuildDirectory> Cleared,
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
        CancellationToken ct = default) =>
        Apply(inspector, candidates, _ => lockFiles, ct);

    /// <param name="lockFilesOf">
    /// The files to ask about for each candidate, for a tool whose lock files are named by the
    /// project rather than in advance. See <see cref="BuildDirectoryKind.ProjectLockFiles"/>.
    /// </param>
    public static LiveTreeVetoResult Apply(
        ILiveTreeInspector inspector,
        IReadOnlyList<RecognisedBuildDirectory> candidates,
        Func<RecognisedBuildDirectory, IReadOnlyList<string>> lockFilesOf,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return new LiveTreeVetoResult([], [], Complete: true);
        }

        var findings = inspector.FindLive(
            [.. candidates.Select(c => new LiveTreeQuery(c.Path, c.Project, lockFilesOf(c)))],
            ct);

        // The same rule for the lock files, not the list it produced, because the list is read from
        // the project and an editor opened after the preview adds to it.
        return new LiveTreeVetoResult(
            [
                .. candidates.Where(c => !findings.IsLive(c.Path)).Select(c => new ClearedBuildDirectory(
                    c.Path,
                    c.Project,
                    new LiveTreeCheck(inspector, c, lockFilesOf))),
            ],
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
    /// <param name="name">
    /// How each held directory is named to the user, or null for the project shape a source tree
    /// wants — <c>obj in MyProject</c>.
    ///
    /// <para>A parameter rather than a second copy of this method, because what differs between one
    /// subject and the next is the naming and not the sentences. A scratch entry in <c>%TEMP%</c>
    /// has no project folder to sit in, so naming it that way would print an empty parent; the
    /// wording either side of the name is the same advice in both cases, and one copy of it is what
    /// keeps the two from drifting into two different pieces of advice.</para>
    /// </param>
    public static PlanNote? NoteFor(IReadOnlyList<LiveTree> vetoed, Func<LiveTree, string>? name = null)
    {
        ArgumentNullException.ThrowIfNull(vetoed);

        if (vetoed.Count == 0)
        {
            return null;
        }

        // Each directory with its own holders, rather than one directory's holders attributed to
        // all of them. This is the sentence the user acts on to decide what to close, so naming the
        // wrong process on it is worse than naming none: it sends them to shut down something
        // innocent and leaves them believing the check misfired when the entry stays on the list.
        var held = vetoed.Select(v =>
            $"{(name is null ? $"{Name(v.Directory)} in {Name(Parent(v.Directory))}" : name(v))} "
            + $"({string.Join("; ", v.Holders)})");

        // Both grammatical forms written out, for the reason ObjPlanNotes records: driving the real
        // window is what catches a sentence that reads correctly only on a machine with more than
        // one of something.
        return new PlanNote(
            PlanNoteSeverity.Warning,
            $"Left {string.Join(", ", held)} alone. " + (vetoed.Count == 1
                ? "Close what is using it and scan again to include it."
                : "Close what is using each one and scan again to include them."));
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
    /// <param name="advice">
    /// What the user is told to do about it, which is the half that belongs to the subject. "Close
    /// any editor or build" is the answer for a source tree and means nothing on a scratch folder,
    /// where there is no project and no build. The sentence in front of it is the same either way,
    /// so it stays here.
    /// </param>
    public static PlanNote? IncompleteNote(bool complete, string advice) => complete
        ? null
        : new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not check whether anything is using these. {advice}");
}
