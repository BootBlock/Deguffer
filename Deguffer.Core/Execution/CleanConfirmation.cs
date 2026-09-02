using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>One line of a <see cref="CleanConfirmation"/>: a named subject and what it frees.</summary>
/// <param name="ProviderName">The named cause, as the preview row calls it (§2).</param>
/// <param name="SizeLabel">
/// What removing it is estimated to reclaim, already qualified where the figure is a forecast
/// rather than a measurement.
/// </param>
public sealed record CleanConfirmationItem(string ProviderName, string SizeLabel);

/// <summary>A listed subject that does not rebuild itself, and what deleting it costs.</summary>
/// <param name="ProviderName">The named cause, as the line listing it calls it.</param>
/// <param name="Consequence">
/// From <see cref="ConfirmationRequirement.ConsequenceOf"/>, so this is the same sentence the
/// subject's own dialog would have carried rather than a second one written for this list.
/// </param>
public sealed record CleanConfirmationLoss(string ProviderName, string Consequence);

/// <summary>
/// What the shell's blanket "are you sure" is about: the selected plans §7 will not put a question
/// of its own to the user about, per <see cref="ConfirmationRequirement.NotPromptedFor{T}"/>.
///
/// It is a Core type for the same reason <see cref="ConfirmationRequirement"/> is — what the user
/// is told before a deletion is a decision, not a layout. The shell arranges these parts and
/// supplies the sentences that hold for every subject; it does not get to choose what the parts
/// claim. Keeping the subjects and the total apart is also what lets the dialog list one line per
/// subject rather than run them together into a single sentence nobody can check at a glance.
/// </summary>
public sealed record CleanConfirmation
{
    /// <remarks>
    /// Private so <see cref="For"/> is the only way to make one, which is what keeps
    /// <see cref="TotalLabel"/> a fact about <see cref="Items"/> rather than a claim a caller
    /// supplies. It also keeps the type out of the XAML type-info generator's activator table,
    /// which emits <c>new CleanConfirmation()</c> for any bound type that offers a public
    /// parameterless constructor, and does not compile when the members it would leave unset are
    /// required.
    /// </remarks>
    private CleanConfirmation(
        IReadOnlyList<CleanConfirmationItem> items,
        IReadOnlyList<CleanConfirmationLoss> permanentLosses,
        string totalLabel)
    {
        Items = items;
        PermanentLosses = permanentLosses;
        TotalLabel = totalLabel;
    }

    /// <summary>One per subject, in the order they were selected.</summary>
    public IReadOnlyList<CleanConfirmationItem> Items { get; }

    /// <summary>
    /// Of <see cref="Items"/>, those that do not rebuild themselves, and what each one loses.
    ///
    /// Ordinarily empty, because §7 leaves only Tier 1 to this dialog. It stops being empty where
    /// the user has switched the typed phrase off: Tier 3 then asks nothing of its own and arrives
    /// here instead, and "everything listed rebuilds itself" is false of those rows. §7 leaves how
    /// hard the confirmation is to give to the user; it never left saying what is unrecoverable to
    /// anybody.
    ///
    /// Keyed on "not Tier 1" rather than on Tier 3, so a tier that reaches this dialog without
    /// anyone deciding it should stands the reassurance down rather than inheriting it. That is
    /// §5.2's direction: an unrecognised thing must not come out treated as safe.
    /// </summary>
    public IReadOnlyList<CleanConfirmationLoss> PermanentLosses { get; }

    /// <summary>
    /// Whether every listed subject rebuilds itself, and so whether the shell may say so. False the
    /// moment <see cref="PermanentLosses"/> is not empty.
    /// </summary>
    public bool AllRegenerable => PermanentLosses.Count == 0;

    /// <summary>
    /// The sum of <see cref="Items"/> and of nothing else.
    ///
    /// Derived rather than taken from the caller, which is most of the point of the type: in a
    /// mixed selection the screen's own total includes the rows §7 asks about separately, and a
    /// dialog quoting a figure larger than the deletions it authorises is describing something the
    /// user is not being asked to approve.
    /// </summary>
    public string TotalLabel { get; }

    public static CleanConfirmation For(IReadOnlyList<CleanupPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        return new CleanConfirmation(
            [.. plans.Select(p => new CleanConfirmationItem(p.ProviderName, FreeSpace.Format(p.Estimated)))],
            [
                .. plans
                    .Where(p => p.Tier != SafetyTier.RegenerableCache)
                    .Select(p => new CleanConfirmationLoss(
                        p.ProviderName, ConfirmationRequirement.ConsequenceOf(p))),
            ],
            FreeSpace.Format(plans.Aggregate(ScanSize.Zero, (total, p) => total + p.Estimated)));
    }
}
