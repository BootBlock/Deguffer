using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The one confirmation a clean asks (§7): every selected row that has something to remove, grouped by
/// tier with its size, what each row needs before it may go, the total, and what follows the clean.
///
/// <para><b>One question per clean.</b> A series of near-identical modals, each with a Cancel whose
/// meaning changes, is the gate §7 warns users learn to get past without reading. So every row is on
/// one screen, the total is the total of everything going, Cancel stops the whole clean, and a row the
/// user no longer wants is unticked rather than skipped.</para>
///
/// <para><b>A Core type</b> because what the user is told before a deletion, and what their answers
/// authorise, are decisions rather than layout. The shell arranges these parts and collects the
/// <see cref="CleanConfirmationReply"/>s. It does not decide whether they suffice. The planner still
/// re-derives every requirement and refuses a plan whose answer is missing, so this is the first of two
/// checks, not the only one.</para>
/// </summary>
public sealed record CleanConfirmation
{
    /// <remarks>
    /// Private so <see cref="For"/> is the only way to make one, which keeps every figure a fact about
    /// <see cref="Items"/> rather than a claim a caller supplies. It also keeps the type out of the XAML
    /// type-info generator's activator table, which emits <c>new CleanConfirmation()</c> for any bound
    /// type that offers a public parameterless constructor.
    /// </remarks>
    private CleanConfirmation(IReadOnlyList<CleanConfirmationItem> items, int refused, string? then)
    {
        Items = items;
        Refused = refused;
        Then = then;
        Groups =
        [
            .. items
                .GroupBy(item => item.Tier)
                .OrderBy(group => group.Key)
                .Select(group => new CleanConfirmationGroup(group.Key, [.. group])),
        ];
        Opening = [.. items.Select(item => new CleanConfirmationReply(item.ProviderId, Included: true))];
    }

    /// <summary>Every row with something to remove, in the order selected.</summary>
    public IReadOnlyList<CleanConfirmationItem> Items { get; }

    /// <summary><see cref="Items"/> by tier, from the least to the most serious loss.</summary>
    public IReadOnlyList<CleanConfirmationGroup> Groups { get; }

    /// <summary>
    /// How many selected rows §3 excludes (Tier 4). No answer authorises one, so none is listed or asked
    /// about. <see cref="Authorise"/> counts them as declined, so a run can tell a refusal from a
    /// selection with nothing to remove.
    /// </summary>
    internal int Refused { get; }

    /// <summary>
    /// What follows the clean, where the user chose anything but doing nothing. The choice is stored and
    /// can be days old, so the question that starts the clean says what will happen when it ends.
    /// </summary>
    public string? Then { get; }

    /// <summary>
    /// The answers the dialog opens with: every row ticked, nothing acknowledged and nothing typed. Also
    /// what a clean that asks nothing authorises, which is every row, since none of them needs an answer.
    /// </summary>
    public IReadOnlyList<CleanConfirmationReply> Opening { get; }

    /// <summary>
    /// Whether any row removes files rather than only releasing local copies, and so whether the dialog
    /// may say that what goes cannot be undone.
    /// </summary>
    public bool RemovesFiles => Items.Any(item => item.Requirement.Verb != ConfirmationVerb.Release);

    /// <summary>The last sentence before the buttons, where <see cref="RemovesFiles"/> holds.</summary>
    public static string ClosingLine => "What a clean removes goes straight away. This cannot be undone.";

    /// <summary>
    /// Whether the question is put to the user at all.
    ///
    /// <para>Always where a row needs an answer of its own: §7 makes Tier 2's acknowledgement and, with
    /// the preference on, Tier 3's typed phrase conditions of deleting, not preferences about asking.
    /// Otherwise only where the user wants to confirm before cleaning, which they can switch off. A
    /// selection with nothing to remove asks nothing.</para>
    /// </summary>
    public bool MustAsk(bool confirmBeforeCleaning) =>
        Items.Count > 0
        && (confirmBeforeCleaning || Items.Any(item => item.Requirement.Level != ConfirmationLevel.None));

    /// <summary>
    /// Whether <paramref name="replies"/> let the clean start: at least one row is still ticked, every
    /// ticked Tier 2 row is acknowledged, and every ticked typed box holds its phrase. An unticked row
    /// needs nothing, because it is not going.
    /// </summary>
    public bool IsSatisfiedBy(IReadOnlyList<CleanConfirmationReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        var anyIncluded = false;

        foreach (var item in Items)
        {
            if (Included(item, replies) is not { } reply)
            {
                continue;
            }

            if (!item.IsSatisfiedBy(reply))
            {
                return false;
            }

            anyIncluded = true;
        }

        return anyIncluded;
    }

    /// <summary>
    /// What the ticked rows free together, with space two of them share counted once (see
    /// <see cref="ReclaimPool"/>). The rows the user unticked are not in it: the figure is what the
    /// button authorises.
    /// </summary>
    public string TotalOf(IReadOnlyList<CleanConfirmationReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        return FreeSpace.Format(
            ReclaimPool.Total(Items.Where(item => Included(item, replies) is not null).SelectMany(item => item.Plan.Steps)));
    }

    /// <summary>
    /// The word for the ticked rows together: <see cref="ConfirmationVerb.Delete"/> where any of them is
    /// a permanent loss, <see cref="ConfirmationVerb.Release"/> where every one only releases, and
    /// <see cref="ConfirmationVerb.Clean"/> otherwise.
    /// </summary>
    internal ConfirmationVerb VerbOf(IReadOnlyList<CleanConfirmationReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        ConfirmationVerb[] verbs = [.. Items.Where(item => Included(item, replies) is not null).Select(item => item.Requirement.Verb)];

        if (verbs.Contains(ConfirmationVerb.Delete))
        {
            return ConfirmationVerb.Delete;
        }

        return verbs.Length > 0 && verbs.All(verb => verb == ConfirmationVerb.Release)
            ? ConfirmationVerb.Release
            : ConfirmationVerb.Clean;
    }

    /// <summary>The question, which opens on the verb its button repeats.</summary>
    public string TitleOf(IReadOnlyList<CleanConfirmationReply> replies) => VerbOf(replies) switch
    {
        ConfirmationVerb.Delete => "Delete these items?",
        ConfirmationVerb.Release => "Release these local copies?",
        _ => "Clean these locations?",
    };

    /// <summary>The button that starts the clean: the verb and the figure it authorises, such as "Delete 32.1 GB".</summary>
    public string ConfirmLabelOf(IReadOnlyList<CleanConfirmationReply> replies) =>
        $"{VerbOf(replies).ToWord()} {TotalOf(replies)}";

    /// <summary>
    /// What <paramref name="replies"/> let through: the ticked rows, in the order selected, and the
    /// answers that authorise them.
    /// </summary>
    /// <exception cref="ConfirmationRequiredException">
    /// A ticked row whose requirement the replies do not satisfy. The shell enables its button only on
    /// <see cref="IsSatisfiedBy"/>, so reaching this is a bug, and failing closed is the half of it that
    /// keeps the user's data.
    /// </exception>
    public Authorisation Authorise(IReadOnlyList<CleanConfirmationReply> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);

        List<Finding> authorised = [];
        List<Confirmation> confirmations = [];
        var declined = Refused;

        foreach (var item in Items)
        {
            if (Included(item, replies) is not { } reply)
            {
                declined++;
                continue;
            }

            if (!item.IsSatisfiedBy(reply))
            {
                throw new ConfirmationRequiredException(item.Requirement);
            }

            authorised.Add(item.Finding);

            if (item.AnswerOf(reply) is { } answer)
            {
                confirmations.Add(answer);
            }
        }

        return new Authorisation(authorised, confirmations, declined);
    }

    /// <summary>
    /// The confirmation for <paramref name="selected"/>.
    ///
    /// <list type="bullet">
    /// <item>A row with no plan, or an empty one, destroys nothing, and is neither listed nor run.</item>
    /// <item>Tier 4 is counted in <see cref="Refused"/> and never listed. No answer authorises it, so
    /// listing it would offer a tick whose every value means no.</item>
    /// <item>Every other row is listed with what §7 asks of it.</item>
    /// </list>
    /// </summary>
    /// <param name="requireTypedPhrase">The user's preference about §7's typed phrase. See <see cref="ConfirmationRequirement.For"/>.</param>
    /// <param name="then">What the user chose to follow a clean.</param>
    public static CleanConfirmation For(IReadOnlyList<Finding> selected, bool requireTypedPhrase, CompletionAction then)
    {
        ArgumentNullException.ThrowIfNull(selected);

        List<CleanConfirmationItem> items = [];
        var refused = 0;

        foreach (var finding in selected)
        {
            if (finding.Plan is not { IsEmpty: false } plan)
            {
                continue;
            }

            var requirement = ConfirmationRequirement.For(plan, requireTypedPhrase);

            if (requirement.Level == ConfirmationLevel.Refused)
            {
                refused++;
                continue;
            }

            items.Add(new CleanConfirmationItem(finding, plan, requirement));
        }

        return new CleanConfirmation(items, refused, WhenCleanComplete.Then(then));
    }

    /// <summary>
    /// The reply that keeps <paramref name="item"/> in the clean, or null where it is unticked or has no
    /// reply. A missing reply leaves the row out, so a shell that forgets a row fails closed.
    /// </summary>
    private static CleanConfirmationReply? Included(CleanConfirmationItem item, IReadOnlyList<CleanConfirmationReply> replies) =>
        replies.FirstOrDefault(reply => string.Equals(reply.ProviderId, item.ProviderId, StringComparison.Ordinal)) is { Included: true } reply
            ? reply
            : null;
}
