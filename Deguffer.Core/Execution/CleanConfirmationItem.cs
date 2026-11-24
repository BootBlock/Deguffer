using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// One row of a <see cref="CleanConfirmation"/>: a selected location, what removing it frees, and what
/// §7 asks of the user before it goes.
/// </summary>
public sealed record CleanConfirmationItem
{
    /// <remarks>
    /// Internal so <see cref="CleanConfirmation.For"/> is the only way to make one, which keeps
    /// <see cref="Requirement"/> the one §7 derives from the plan rather than one a caller supplies.
    /// </remarks>
    internal CleanConfirmationItem(Finding finding, CleanupPlan plan, ConfirmationRequirement requirement)
    {
        Finding = finding;
        Plan = plan;
        Requirement = requirement;
    }

    /// <summary>The selected row, which <see cref="CleanConfirmation.Authorise"/> hands to the run.</summary>
    internal Finding Finding { get; }

    internal CleanupPlan Plan { get; }

    internal ConfirmationRequirement Requirement { get; }

    public string ProviderId => Requirement.ProviderId;

    /// <summary>The named cause, as the preview row calls it (§2).</summary>
    public string ProviderName => Requirement.ProviderName;

    public SafetyTier Tier => Requirement.Tier;

    /// <summary>
    /// What removing it is estimated to free, already qualified where the figure is a forecast rather
    /// than a measurement.
    /// </summary>
    public string SizeLabel => FreeSpace.Format(Plan.Reclaim);

    /// <summary>
    /// The include box's accessible name: the visible name first, then the figure beside it, so a
    /// screen reader hears what the sighted reader sees on the row.
    /// </summary>
    public string IncludeName => $"{ProviderName}, {SizeLabel}";

    /// <summary>
    /// What removing it costs, for every row that does not rebuild itself, and null for Tier 1, whose
    /// group says it once for all of them.
    ///
    /// <para>Keyed on "not Tier 1" rather than on a list of tiers, so the reassurance is given only where
    /// it is known to hold, which is §5.2's direction. It holds where the typed phrase is switched off,
    /// too: §7 leaves how hard the confirmation is to give to the user, and never left saying what is
    /// unrecoverable to anybody.</para>
    /// </summary>
    public string? Consequence => Tier == SafetyTier.RegenerableCache ? null : Requirement.Consequence;

    /// <summary>
    /// The words beside a Tier 2 row's acknowledgement, naming the row so each box is a decision about
    /// one location, or null where the row asks for none.
    /// </summary>
    public string? Acknowledgement =>
        Requirement.Level == ConfirmationLevel.Acknowledgement
            ? $"I accept the time it takes to restore {ProviderName}"
            : null;

    /// <summary>What the row says around its typed box, or null where nothing is typed.</summary>
    public PhraseWording? Typing => Requirement.Typing;

    /// <summary>
    /// The answer <paramref name="reply"/> gives this row's requirement, in the form the planner checks
    /// again before it deletes, or null where it gives none.
    /// </summary>
    internal Confirmation? AnswerOf(CleanConfirmationReply reply) => Requirement.Level switch
    {
        ConfirmationLevel.Acknowledgement when reply.Acknowledged => new Confirmation(ProviderId),
        ConfirmationLevel.TypedPhrase => new Confirmation(ProviderId, reply.TypedPhrase),
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="reply"/> satisfies this row. Decided by
    /// <see cref="ConfirmationRequirement.IsSatisfiedBy"/>, the check the planner makes, so the dialog's
    /// button and the executor cannot disagree.
    /// </summary>
    internal bool IsSatisfiedBy(CleanConfirmationReply reply) =>
        Requirement.IsSatisfiedBy(AnswerOf(reply) is { } answer ? [answer] : []);
}
