namespace Deguffer.Core.Execution;

/// <summary>
/// What the user did with one row of a <see cref="CleanConfirmation"/>. The shell collects these and
/// Core decides what they authorise, so the dialog cannot come to disagree with the planner about
/// whether a row was confirmed.
/// </summary>
/// <param name="ProviderId">The row's plan, as <see cref="Confirmation.ProviderId"/> names it.</param>
/// <param name="Included">
/// Whether the row is still ticked. Unticking a row is how the user leaves it out of this clean; Cancel
/// stops the whole clean.
/// </param>
/// <param name="Acknowledged">Whether a Tier 2 row's acknowledgement is ticked. Read for no other tier.</param>
/// <param name="TypedPhrase">What was typed into a Tier 3 row's box. Read only where the row asks for typing.</param>
public sealed record CleanConfirmationReply(
    string ProviderId,
    bool Included,
    bool Acknowledged = false,
    string? TypedPhrase = null);
