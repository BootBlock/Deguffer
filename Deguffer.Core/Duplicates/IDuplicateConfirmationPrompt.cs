namespace Deguffer.Core.Duplicates;

/// <summary>
/// Puts a duplicate removal to the user. Every word it shows comes from the
/// <see cref="RemovalConfirmation"/>: the title, the summary, each warning and every copy that goes;
/// this seam only carries it to a surface that can ask.
///
/// Separate from Explore's prompt, because this one must list every copy, and a copy is not an item
/// the user picked out of a picture: a rule may have marked it (§7.4).
/// </summary>
public interface IDuplicateConfirmationPrompt
{
    /// <summary>Whether the user said yes. Declining is a decision, not a failure.</summary>
    Task<bool> AskAsync(RemovalConfirmation confirmation, CancellationToken ct = default);
}
