namespace Deguffer.Core.Execution;

/// <summary>What happened to one step.</summary>
/// <param name="Skipped">
/// §5.3: access denied is not a failure. A locked file is the OS protecting live state, and it
/// is recorded rather than escalated.
/// </param>
/// <param name="Kept">
/// Files left alone because the user asked for anything touched recently to be left. Reported apart
/// from <paramref name="Skipped"/> because it is a setting being honoured rather than Windows
/// refusing, and only one of the two is something the user might want to act on.
/// </param>
public sealed record StepOutcome(
    string Description,
    bool Succeeded,
    long BytesReclaimed,
    int Skipped,
    string? Message = null,
    int Kept = 0);

/// <summary>The outcome of executing a plan, including the §5.6 verification.</summary>
public sealed record CleanupResult
{
    public required string ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public IReadOnlyList<StepOutcome> Steps { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public VerificationResult? Verification { get; init; }

    public long BytesReclaimed => Steps.Sum(s => s.BytesReclaimed);

    /// <summary>Items left in place because something held them open (§5.3).</summary>
    public int SkippedCount => Steps.Sum(s => s.Skipped);

    /// <summary>Files left alone because they had been touched inside the user's guard window.</summary>
    public int KeptCount => Steps.Sum(s => s.Kept);

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
    /// </summary>
    public IReadOnlyList<VerificationCheck> Failures =>
        [.. Checks.Where(c => c.Outcome == VerificationOutcome.Failed)];

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
