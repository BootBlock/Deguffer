using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>How hard the user has to work to authorise a plan (§7).</summary>
public enum ConfirmationLevel
{
    /// <summary>Tier 1. Selecting the row is the whole decision; nothing is lost either way.</summary>
    None,

    /// <summary>
    /// Tier 2. A deliberate yes, because the cost is real — gigabytes re-downloaded, minutes
    /// re-indexed — even though nothing is destroyed.
    /// </summary>
    Acknowledgement,

    /// <summary>
    /// Tier 3. §7: "Tier 3 requires typed confirmation, and says plainly what is unrecoverable."
    /// The user types <see cref="ConfirmationRequirement.RequiredPhrase"/> to proceed.
    /// </summary>
    TypedPhrase,

    /// <summary>
    /// Tier 4. No confirmation authorises this — §3 excludes it from the UI entirely, so arriving
    /// here at all means something upstream offered a row it should not have.
    /// </summary>
    Refused,
}

/// <summary>
/// The user's answer to a requirement. Carries <see cref="ProviderId"/> because a confirmation is
/// for one named subject: an acknowledgement collected for the Android SDK must not authorise a
/// different provider that happened to be selected in the same pass.
/// </summary>
/// <param name="ProviderId">The plan this answer was given for.</param>
/// <param name="TypedPhrase">What the user typed, where the tier demanded typing.</param>
public sealed record Confirmation(string ProviderId, string? TypedPhrase = null);

/// <summary>
/// What a plan needs before it may be executed, decided from its tier alone.
///
/// This is a decision, not a dialog: it lives in Core so the rule is provable without a WinUI host,
/// exactly as <see cref="ElevationOffer"/> is. The shell renders <see cref="Level"/> and
/// <see cref="Consequence"/> and collects the answer; it does not get to choose what is required.
/// </summary>
public sealed record ConfirmationRequirement
{
    public required string ProviderId { get; init; }

    public required string ProviderName { get; init; }

    public required SafetyTier Tier { get; init; }

    public required ConfirmationLevel Level { get; init; }

    /// <summary>Said plainly, and in the irreversible case saying so (§7, §8 q4).</summary>
    public required string Consequence { get; init; }

    /// <summary>
    /// The words the user must type, for <see cref="ConfirmationLevel.TypedPhrase"/> only.
    ///
    /// The provider's own name rather than a constant like "DELETE": a fixed word becomes muscle
    /// memory across rows, whereas typing the name of the thing being destroyed is a decision about
    /// <em>this</em> subject, which is the entire purpose of making the user type at all.
    /// </summary>
    public string? RequiredPhrase { get; init; }

    /// <summary>
    /// Whether §7 will put a question to the user about this plan. An empty plan is never executed
    /// and so is never asked about, which is why emptiness is part of the rule rather than the
    /// caller's business.
    ///
    /// The shell uses this to stand its own blanket confirmation down: asking twice about one
    /// deletion trains people to dismiss the prompt that carries the §7 consequence.
    /// </summary>
    public static bool PromptsUser(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return !plan.IsEmpty && For(plan).Level != ConfirmationLevel.None;
    }

    /// <summary>
    /// Of a selection, the items that will be deleted without §7 putting a question of its own —
    /// what a shell-level "are you sure" must therefore cover to leave nothing unconfirmed.
    ///
    /// This is a decision about the whole selection rather than one plan, which is why it is here
    /// and not left to the caller: treating "does anything here need §7?" as the same question as
    /// "is everything here covered by §7?" deleted a mixed selection's Tier 1 items silently.
    /// </summary>
    public static IReadOnlyList<T> NotPromptedFor<T>(IEnumerable<T> items, Func<T, CleanupPlan?> planOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(planOf);

        return [.. items.Where(i => planOf(i) is { IsEmpty: false } plan && !PromptsUser(plan))];
    }

    public static ConfirmationRequirement For(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var level = plan.Tier switch
        {
            SafetyTier.RegenerableCache => ConfirmationLevel.None,
            SafetyTier.RegenerableWithCost => ConfirmationLevel.Acknowledgement,
            SafetyTier.UserData => ConfirmationLevel.TypedPhrase,
            _ => ConfirmationLevel.Refused,
        };

        return new ConfirmationRequirement
        {
            ProviderId = plan.ProviderId,
            ProviderName = plan.ProviderName,
            Tier = plan.Tier,
            Level = level,
            Consequence = Describe(plan, level),
            RequiredPhrase = level == ConfirmationLevel.TypedPhrase ? plan.ProviderName : null,
        };
    }

    /// <summary>
    /// Whether <paramref name="confirmations"/> authorises this plan. Absent answers do not
    /// authorise anything, which is what makes the caller's omission fail closed.
    /// </summary>
    public bool IsSatisfiedBy(IEnumerable<Confirmation> confirmations)
    {
        ArgumentNullException.ThrowIfNull(confirmations);

        if (Level == ConfirmationLevel.None)
        {
            return true;
        }

        if (Level == ConfirmationLevel.Refused)
        {
            return false;
        }

        var answer = confirmations.FirstOrDefault(
            c => string.Equals(c.ProviderId, ProviderId, StringComparison.Ordinal));

        if (answer is null)
        {
            return false;
        }

        // Trimmed and case-insensitive, deliberately. The typed phrase is a deliberation device,
        // not a password — rejecting a phrase for its capitals would train the user to paste it,
        // which defeats the point rather than strengthening it.
        return Level != ConfirmationLevel.TypedPhrase
            || string.Equals(answer.TypedPhrase?.Trim(), RequiredPhrase, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(CleanupPlan plan, ConfirmationLevel level) => level switch
    {
        ConfirmationLevel.None => plan.WhatHappensOnNextUse,
        ConfirmationLevel.Acknowledgement =>
            $"Nothing is lost permanently, but restoring it costs real time. {plan.WhatHappensOnNextUse}",
        ConfirmationLevel.TypedPhrase =>
            $"This is user data, and deleting it is permanent — it is not sent to the Recycle Bin and " +
            $"cannot be undone. {plan.WhatHappensOnNextUse}",
        _ => $"'{plan.ProviderName}' is {plan.Tier.ToDisplayName()} and is never offered for deletion.",
    };
}
