using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>What every step of one plan's execution shares. See <see cref="PlanExecutor.ExecuteAsync"/>.</summary>
/// <param name="Plan">The plan the steps belong to.</param>
/// <param name="Residue">What the run's removals left standing and left alone, which each removal adds to.</param>
/// <param name="Activity">What the clean has set acting on the machine, which each step adds to.</param>
/// <param name="Deletions">The one bound on the deletions of every step, so steps that run at once share the disk's queue.</param>
/// <param name="Underway">Each command step's tool as it starts, and null as it returns.</param>
internal sealed record PlanRun(
    CleanupPlan Plan,
    RunResidue Residue,
    RunActivity Activity,
    ThreadAllowance Deletions,
    IProgress<CommandUnderway?>? Underway);
