namespace Deguffer.Core.Execution;

/// <summary>What happened to one step.</summary>
/// <param name="Refused">
/// §5.3: a file Windows will not release is not a failure. It is recorded rather than escalated,
/// with its bytes and the reason Windows gave — see <see cref="Refusals"/> for why a count alone was
/// not enough.
/// </param>
/// <param name="Kept">
/// Files left alone because the user asked for anything touched recently to be left. Reported apart
/// from <paramref name="Refused"/> because it is a setting being honoured rather than Windows
/// refusing, and only one of the two is something the user might want to act on.
/// </param>
/// <param name="Spared">
/// Entries left alone because something is using them (§5.3). Kept apart from the two above it, for
/// the reason they are apart: a refusal is Windows declining, a keep is a setting being honoured, and
/// this is Deguffer declining to touch something it found in use. Only this one names the program to
/// close, because the plan found the program before the removal began.
/// </param>
/// <param name="EntriesRemoved">
/// How many entries the step took, for a removal whose reclaim is its entries rather than its bytes —
/// a leftover of empty folders frees nothing measurable and still leaves the disk tidier. Zero for a
/// §5.1 command, which reports what it freed and nothing about what it removed.
/// </param>
/// <param name="RefusedFolders">
/// Folders Windows would not remove for a reason of their own, most often one a program is working
/// in. Apart from <paramref name="Refused"/> because a folder holds no bytes: see
/// <see cref="FolderRefusals"/>.
/// </param>
/// <param name="MailStores">
/// Outlook mail stores the step found and left where they were, because Deguffer never removes one.
/// Apart from every count above, because none of their sentences is true of it: nothing refused, no
/// setting was honoured, and nothing was using the file.
/// </param>
public sealed record StepOutcome(
    string Description,
    bool Succeeded,
    long BytesReclaimed,
    Refusals Refused,
    string? Message = null,
    int Kept = 0,
    int Spared = 0,
    long EntriesRemoved = 0,
    FolderRefusals RefusedFolders = default,
    int MailStores = 0);

/// <summary>The outcome of executing a plan, including the §5.6 verification.</summary>
public sealed record CleanupResult
{
    public required string ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public IReadOnlyList<StepOutcome> Steps { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public VerificationResult? Verification { get; init; }

    public long BytesReclaimed => Steps.Sum(s => s.BytesReclaimed);

    /// <summary>Entries the run took, across every step. See <see cref="StepOutcome.EntriesRemoved"/>.</summary>
    public long EntriesRemoved => Steps.Sum(s => s.EntriesRemoved);

    /// <summary>Files left in place because Windows would not release them (§5.3), by reason.</summary>
    public Refusals Refused => Steps.Aggregate(Refusals.None, (total, step) => total + step.Refused);

    /// <summary>Folders Windows would not remove for a reason of their own, by reason.</summary>
    public FolderRefusals RefusedFolders =>
        Steps.Aggregate(FolderRefusals.None, (total, step) => total + step.RefusedFolders);

    /// <summary>Files left alone because they had been touched inside the user's guard window.</summary>
    public int KeptCount => Steps.Sum(s => s.Kept);

    /// <summary>Entries left alone because something was found to be using them (§5.3).</summary>
    public int SparedCount => Steps.Sum(s => s.Spared);

    /// <summary>Outlook mail stores left where they were. See <see cref="StepOutcome.MailStores"/>.</summary>
    public int MailStoreCount => Steps.Sum(s => s.MailStores);

    public bool Succeeded => Steps.All(s => s.Succeeded);
}

/// <summary>
/// What one §5.6 check established about one protected path.
///
/// An enum rather than a bool, because "it is gone" and "this run is why it is gone" are two
/// different findings and only one of them is an alarm. A plan is made when the user previews and
/// carried out when they clean, so a path can disappear in between for reasons that have nothing to
/// do with Deguffer — and a single pass/fail flag had no way to say which had happened.
/// </summary>
public enum VerificationOutcome
{
    /// <summary>It was not there when the plan was made, so there was nothing to preserve.</summary>
    NotPresentBefore,

    /// <summary>It was there when the plan was made, and it is there now.</summary>
    Survived,

    /// <summary>
    /// It is gone, and this run could have taken it. The alarm §5.6 exists to raise: it means a
    /// rule reached further than it was meant to.
    /// </summary>
    Failed,

    /// <summary>
    /// It is gone, and this run demonstrably did not take it — see
    /// <see cref="PlanVerifier"/> for what "demonstrably" rests on. Reported rather than passed
    /// over, because the run's figures describe a machine that changed underneath them.
    /// </summary>
    RemovedFromOutside,

    /// <summary>
    /// It is still there and everything inside it has gone. The same alarm as
    /// <see cref="Failed"/>, in the shape an emptying leaves rather than the shape a deletion
    /// leaves — see <see cref="ProtectedPath.HeldContentBefore"/> for why the two need telling
    /// apart at all.
    /// </summary>
    Emptied,

    /// <summary>
    /// It is still there, and a removal this run began above it went inside it and could not take
    /// everything it tried to. The same alarm as <see cref="Failed"/>, in the shape a refusal leaves:
    /// whatever the folder still holds, Deguffer's own deletion reached into a path it promised to
    /// leave. See <see cref="RunResidue"/>.
    /// </summary>
    Entered,
}

/// <summary>One assertion about something that should have survived, and how it came out.</summary>
public sealed record VerificationCheck(
    string Path,
    string Reason,
    VerificationOutcome Outcome,
    string Detail);

/// <summary>
/// §5.6: after acting, assert that the things that should have survived did. This is what turns
/// "I think it worked" into evidence.
/// </summary>
public sealed record VerificationResult
{
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];

    /// <summary>
    /// The paths this run has to answer for. Not cached in a backing field — this is a record, and
    /// <c>with</c> copies backing fields, so a cache would outlive a change to
    /// <see cref="Checks"/>.
    ///
    /// <para><see cref="VerificationOutcome.Emptied"/> and <see cref="VerificationOutcome.Entered"/>
    /// count here beside <see cref="VerificationOutcome.Failed"/>, because the three differ only in
    /// what the wreckage looks like: one path was destroyed, one was emptied, and one was gone into,
    /// and all of them mean a rule reached further than it was meant to. Keeping them apart would let
    /// <see cref="Passed"/> report false while <see cref="Summary"/> said every path survived.</para>
    /// </summary>
    public IReadOnlyList<VerificationCheck> Failures =>
        [.. Checks.Where(c => c.Outcome
            is VerificationOutcome.Failed or VerificationOutcome.Emptied or VerificationOutcome.Entered)];

    /// <summary>
    /// The paths something else took while the preview sat on screen. Kept apart from
    /// <see cref="Failures"/> rather than folded into it: one asks the user to report a fault, and
    /// the other asks them to preview again.
    /// </summary>
    public IReadOnlyList<VerificationCheck> RemovedFromOutside =>
        [.. Checks.Where(c => c.Outcome == VerificationOutcome.RemovedFromOutside)];

    /// <summary>
    /// Whether every protected path is accounted for as still standing. An outside removal is not a
    /// pass: nobody verified that path, and saying otherwise is the overstatement §5.6 exists to
    /// stop.
    /// </summary>
    public bool Passed => Checks.All(
        c => c.Outcome is VerificationOutcome.NotPresentBefore or VerificationOutcome.Survived);

    /// <summary>
    /// One sentence for the whole result, which has to account for every path it could not verify.
    ///
    /// The mixed case gets both counts rather than only the alarming one. Naming the failures alone
    /// would say "1 of 7 did not survive" about a run where six went unverified, and a §5.6 report
    /// that states less than it established is the overstatement's mirror image.
    /// </summary>
    public string Summary => (Checks.Count, Failures.Count, RemovedFromOutside.Count) switch
    {
        (0, _, _) => "Nothing to verify.",
        (var total, 0, 0) => $"All {total} protected path(s) survived.",
        (var total, 0, var outside) =>
            $"{outside} of {total} protected path(s) were removed from outside this run.",
        (var total, var failed, 0) => $"{failed} of {total} protected path(s) did not survive.",
        (var total, var failed, var outside) =>
            $"{failed} of {total} protected path(s) did not survive, and {outside} more were "
            + "removed from outside this run.",
    };
}
