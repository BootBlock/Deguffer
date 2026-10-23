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

/// <summary>
/// What a provider asks the veto about each directory, built from one reading of the machine.
///
/// <para>Built again for every reading rather than kept, because what it holds is read from the
/// machine as well as from the project: the check at the clean reads the process table again once
/// the preview's is past <see cref="IUseCheck.ReadingLimit"/>, and a question built from the
/// preview's reading would ask about the preview's machine. A provider hands
/// the veto the rule that builds one, never one already built.</para>
/// </summary>
/// <param name="Ask">The query for one directory.</param>
internal sealed record LiveTreeQuestion(Func<RecognisedBuildDirectory, LiveTreeQuery> Ask)
{
    /// <summary>
    /// Project folders this reading found named somewhere a program is, which Explore takes as
    /// candidates because walking up from where a program is never reaches them. See
    /// <see cref="InUseBuildDirectories"/>.
    /// </summary>
    public IReadOnlyList<string> NamedProjects { get; init; } = [];

    /// <summary>
    /// A question about a directory's project and the files its tool holds open, which is every
    /// provider's but <c>obj</c>'s.
    /// </summary>
    public static LiveTreeQuestion ForLockFiles(Func<RecognisedBuildDirectory, IReadOnlyList<string>> lockFilesOf) =>
        new(candidate => new LiveTreeQuery(candidate.Path, candidate.Project, lockFilesOf(candidate)));
}

/// <param name="Cleared">
/// The directories a plan may go on to target. Empty where the answer was not whole: a directory the
/// veto could not ask about is not one it found unused.
/// </param>
/// <param name="Vetoed">The directories something is using, and what is using each.</param>
/// <param name="Unchecked">
/// The directories held back because the answer was not whole, among those it did not find in use.
/// </param>
/// <param name="Complete">False where liveness could not be established in full.</param>
public sealed record LiveTreeVetoResult(
    IReadOnlyList<ClearedBuildDirectory> Cleared,
    IReadOnlyList<LiveTree> Vetoed,
    IReadOnlyList<string> Unchecked,
    bool Complete)
{
    /// <summary>
    /// Every directory the veto held back, with why, for the §5.6 report: the ones in use and the ones
    /// it could not ask about. One list, so a provider cannot assert the first and forget the second.
    /// </summary>
    public IReadOnlyList<(string Path, string Reason)> Survivors =>
    [
        .. Vetoed.Select(vetoed => (vetoed.Directory, LiveTreeVeto.ProtectedReason)),
        .. Unchecked.Select(path => (path, LiveTreeVeto.UncheckedReason)),
    ];

    /// <summary>Every directory the veto held back, for a caller that leaves them out by path.</summary>
    public IReadOnlyList<string> Held => [.. Vetoed.Select(vetoed => vetoed.Directory), .. Unchecked];
}

/// <summary>
/// §5.3 applied to a source tree: a directory something is using is never a target.
///
/// <para>Written once for every provider that walks source roots, because it is a safety rule rather
/// than a shape. Six copies of "ask, then partition" would be six chances for one of them to warn
/// where it should refuse — and the difference between those two is the whole reason this exists.
/// A cache a tool is still writing to costs a slower next use. A build directory removed under a
/// live editor breaks the work in progress.</para>
///
/// <para><b>An answer that is not whole offers nothing.</b> "Could not tell" and "nothing is using
/// it" are different answers, and only the second is permission: §3 puts whatever the tool cannot
/// prove idle in Tier 4. The likely cause lasts rather than passes: the process table is read at
/// offsets a Windows update could move, which turns the working-directory signal off on that machine
/// for good, so offering under a warning would offer every directory there on no evidence at all. The
/// check at the clean keeps the same rule (<see cref="AtClean"/>).</para>
/// </summary>
internal static class LiveTreeVeto
{
    /// <summary>Why a vetoed directory is listed as a survivor, in the §5.6 report.</summary>
    public const string ProtectedReason = "Something is using this project right now, so it is left alone.";

    /// <summary>
    /// Why a place is held back where the inspector could not tell whether anything is using it, as an
    /// <see cref="InUseNow.Reason"/>.
    /// </summary>
    public const string CannotTell = "Deguffer could not tell whether a running program is using this";

    /// <summary>Why a place held back on an answer that was not whole is listed as a survivor.</summary>
    public const string UncheckedReason = CannotTell + ", so it is left alone.";

    public static LiveTreeVetoResult Apply(
        ILiveTreeInspector inspector,
        IReadOnlyList<RecognisedBuildDirectory> candidates,
        IReadOnlyList<string> lockFiles,
        CancellationToken ct = default) =>
        Apply(inspector, candidates, _ => LiveTreeQuestion.ForLockFiles(_ => lockFiles), ct);

    /// <param name="questions">
    /// The rule that builds the question for one reading of the machine, for a provider whose evidence
    /// is found in the project or on the machine rather than named in advance: lock files a tool names
    /// after the project (see <see cref="BuildDirectoryKind.ProjectLockFiles"/>), or the solutions that
    /// open it (see <see cref="LiveTreeQuery.Workspaces"/>).
    /// </param>
    public static LiveTreeVetoResult Apply(
        ILiveTreeInspector inspector,
        IReadOnlyList<RecognisedBuildDirectory> candidates,
        Func<CancellationToken, LiveTreeQuestion> questions,
        CancellationToken ct = default) =>
        candidates.Count == 0
            ? Nothing
            : Apply(inspector, candidates, questions(ct), questions, ct);

    /// <param name="question">
    /// The question for this reading, for a caller that has built it already to read something else
    /// from the same reading.
    /// </param>
    /// <param name="questions">The rule that built it, which each check at the clean applies again.</param>
    public static LiveTreeVetoResult Apply(
        ILiveTreeInspector inspector,
        IReadOnlyList<RecognisedBuildDirectory> candidates,
        LiveTreeQuestion question,
        Func<CancellationToken, LiveTreeQuestion> questions,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return Nothing;
        }

        var findings = inspector.FindLive([.. candidates.Select(question.Ask)], ct);
        var unused = candidates.Where(c => !findings.IsLive(c.Path)).ToList();

        // The same rule for the question, not the question it produced, because the question is read
        // from the project and the machine, and an editor opened after the preview changes both.
        return new LiveTreeVetoResult(
            [
                .. unused
                    .Where(c => findings.IsAnswered(c.Path))
                    .Select(c => new ClearedBuildDirectory(c.Path, c.Project, new LiveTreeCheck(inspector, c, questions))),
            ],
            findings.Live,
            [.. unused.Where(c => !findings.IsAnswered(c.Path)).Select(c => c.Path)],
            findings.Complete);
    }

    private static readonly LiveTreeVetoResult Nothing = new([], [], [], Complete: true);

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

    /// <summary>
    /// What a check at the clean answers for <paramref name="step"/>, from what it found in use now.
    ///
    /// <para><b>Held back on any answer that is not whole.</b> A plan offers a step only on a whole
    /// answer, so a partial one at the clean leaves the step on evidence the clean no longer has, and a
    /// question that could not be asked is not a clear answer. A preview that read every program can
    /// be followed by a clean that cannot, and running the step then would rest on an answer the user
    /// was never shown.</para>
    /// </summary>
    /// <param name="live">The places found in use now.</param>
    /// <param name="complete">Whether the answer now is whole.</param>
    public static IReadOnlyList<InUseNow> AtClean(DeleteStep step, IReadOnlyList<InUseNow> live, bool complete) =>
        complete ? live : [.. live, new InUseNow(step.Path, CannotTell)];

    private static string Name(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    private static string Parent(string path) =>
        Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;

    /// <summary>
    /// The note for the places held back because the check could not run in full, or null where none
    /// were.
    ///
    /// §5.5 makes a measurement fallback observable for the same reason this is said out loud: a
    /// safeguard that could not run must not look like a safeguard that found nothing, and a row that
    /// leaves out what the disk holds must say why.
    /// </summary>
    /// <param name="count">How many places were held back for it.</param>
    public static PlanNote? UncheckedNote(int count) => count == 0
        ? null
        : new PlanNote(
            PlanNoteSeverity.Warning,
            count == 1
                ? "Deguffer could not check whether anything is using one of these, so it left it alone. "
                  + "Scan again to include it."
                : $"Deguffer could not check whether anything is using {count} of these, so it left them "
                  + "alone. Scan again to include them.");
}
