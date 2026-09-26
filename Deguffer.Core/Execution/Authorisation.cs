namespace Deguffer.Core.Execution;

/// <summary>
/// What §7 let through of a selection: the findings a run may clean, and the answers that
/// authorise them.
///
/// <para>Collected before any work starts, because a question asked mid-deletion is asked after the
/// point its answer could still change anything. Declining is a decision rather than a failure: that
/// finding is dropped and the rest go ahead, the way a dismissed UAC prompt leaves the app running
/// unelevated.</para>
///
/// <para>Here rather than in the shell because which findings go ahead is a decision about what gets
/// deleted, and the shell's part is only to put a question on screen. The planner still re-derives
/// every requirement and refuses a plan whose answer is missing, so this is the first of two
/// checks, not the only one.</para>
/// </summary>
/// <param name="Authorised">The findings to run, in the order they were selected.</param>
/// <param name="Confirmations">The answers given, one for each finding that was asked about and agreed.</param>
/// <param name="Declined">
/// How many findings with something to remove were not authorised: declined by the user, or refused
/// by §3. What tells a run the user stopped from one that had nothing to stop.
/// </param>
public sealed record Authorisation(
    IReadOnlyList<Finding> Authorised,
    IReadOnlyList<Confirmation> Confirmations,
    int Declined)
{
    /// <summary>
    /// Ask whatever §7 requires of each finding in <paramref name="selected"/>, one at a time and in
    /// order, and keep the ones that were answered.
    ///
    /// <list type="bullet">
    /// <item>A finding with no plan, or an empty one, destroys nothing and is neither asked about
    /// nor run.</item>
    /// <item>One that needs no confirmation is authorised without a question.</item>
    /// <item>Tier 4 is dropped without one. No answer authorises it, so asking would be a question
    /// whose every reply means no.</item>
    /// <item>Everything else runs only where <paramref name="ask"/> returns an answer.</item>
    /// </list>
    /// </summary>
    /// <param name="requireTypedPhrase">The user's preference about §7's typed phrase. See <see cref="ConfirmationRequirement.For"/>.</param>
    /// <param name="ask">
    /// Puts one requirement to the user and returns their answer, or null where they declined.
    /// </param>
    public static async Task<Authorisation> CollectAsync(
        IReadOnlyList<Finding> selected,
        bool requireTypedPhrase,
        Func<ConfirmationRequirement, CancellationToken, Task<Confirmation?>> ask,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(ask);

        List<Finding> authorised = [];
        List<Confirmation> confirmations = [];
        var declined = 0;

        foreach (var finding in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

            if (requirement.Level == ConfirmationLevel.None)
            {
                authorised.Add(finding);
                continue;
            }

            // Awaited on the caller's context rather than with ConfigureAwait(false), as the rest of
            // Core is: each question is a dialog, and the next one has to be raised from the UI
            // thread the first was answered on.
            if (requirement.Level == ConfirmationLevel.Refused
                || await ask(requirement, ct) is not { } answer)
            {
                declined++;
                continue;
            }

            authorised.Add(finding);
            confirmations.Add(answer);
        }

        return new Authorisation(authorised, confirmations, declined);
    }
}
