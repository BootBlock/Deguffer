namespace Deguffer.Core.Execution;

/// <summary>What a clean is doing with the plan it has started on. See <see cref="PlanUnderway"/>.</summary>
public enum PlanStage
{
    /// <summary>Carrying out the plan's steps: something is being removed.</summary>
    Removing,

    /// <summary>
    /// Only proving that what the plan promised survived (§5.6): a row the run does not clean, or one
    /// the clean was cancelled before starting. Nothing is removed.
    /// </summary>
    Checking,
}
