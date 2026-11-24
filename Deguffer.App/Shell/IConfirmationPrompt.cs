using Deguffer.Core.Execution;

namespace Deguffer.App.Shell;

/// <summary>
/// Puts a clean's one confirmation to the user. What is asked, and what the answers authorise, are
/// decided in Core; this seam only carries the question to a surface that can ask it, and the answers
/// back.
///
/// It exists so the view-model's clean flow can be reasoned about without a dialog: an implementation
/// that always declines and one that always agrees are both a few lines.
/// </summary>
public interface IConfirmationPrompt
{
    /// <summary>
    /// Returns what the user did with each row, or <see langword="null"/> where they cancelled, which
    /// stops the whole clean with nothing removed.
    /// </summary>
    Task<IReadOnlyList<CleanConfirmationReply>?> AskAsync(CleanConfirmation confirmation, CancellationToken ct = default);
}
