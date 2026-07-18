using Deguffer.Core.Execution;

namespace Deguffer.App.Shell;

/// <summary>
/// Asks the user to satisfy a §7 requirement. The requirement itself is decided in Core; this seam
/// only carries the question to a surface that can ask it, and the answer back.
///
/// It exists so the view-model's clean flow can be reasoned about without a dialog: an
/// implementation that always declines and one that always accepts are both a few lines.
/// </summary>
public interface IConfirmationPrompt
{
    /// <summary>
    /// Returns the user's answer, or <see langword="null"/> if they declined. A declined
    /// confirmation is a decision, not a failure — the caller skips that provider and carries on.
    /// </summary>
    Task<Confirmation?> AskAsync(ConfirmationRequirement requirement, CancellationToken ct = default);
}
