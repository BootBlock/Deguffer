namespace Deguffer.Core.Execution;

/// <summary>
/// What §7 let through of a selection: the findings a run may clean, and the answers that authorise
/// them. Made by <see cref="CleanConfirmation.Authorise"/> from the user's one answer to the clean.
///
/// <para>Collected before any work starts, because a question asked mid-deletion is asked after the
/// point its answer could still change anything.</para>
/// </summary>
/// <param name="Authorised">The findings to run, in the order they were selected.</param>
/// <param name="Confirmations">The answers given, one for each authorised finding §7 asks something of.</param>
/// <param name="Declined">
/// How many findings with something to remove were not authorised: unticked by the user, or refused by
/// §3. What tells a run the user stopped from one that had nothing to stop.
/// </param>
public sealed record Authorisation(
    IReadOnlyList<Finding> Authorised,
    IReadOnlyList<Confirmation> Confirmations,
    int Declined);
