namespace Deguffer.Core.Execution;

/// <summary>What happened to one step.</summary>
/// <param name="Skipped">
/// §5.3: access denied is not a failure. A locked file is the OS protecting live state, and it
/// is recorded rather than escalated.
/// </param>
public sealed record StepOutcome(
    string Description,
    bool Succeeded,
    long BytesReclaimed,
    int Skipped,
    string? Message = null);

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

    public bool Succeeded => Steps.All(s => s.Succeeded);
}

/// <summary>One assertion that something survived.</summary>
public sealed record VerificationCheck(string Path, string Reason, bool Passed, string Detail);

/// <summary>
/// §5.6: after acting, assert that the things that should have survived did. This is what turns
/// "I think it worked" into evidence.
/// </summary>
public sealed record VerificationResult
{
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];

    public bool Passed => Checks.All(c => c.Passed);

    public IEnumerable<VerificationCheck> Failures => Checks.Where(c => !c.Passed);

    public string Summary => Checks.Count == 0
        ? "Nothing to verify."
        : Passed
            ? $"All {Checks.Count} protected path(s) survived."
            : $"{Failures.Count()} of {Checks.Count} protected path(s) did not survive.";
}
