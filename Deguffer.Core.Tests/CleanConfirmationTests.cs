using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a clean's one confirmation puts in front of the user, and what the answers to it authorise,
/// without a WinUI host: the dialog's layout is the shell's business, but what it claims and what it
/// lets through are decided here.
/// </summary>
public sealed class CleanConfirmationTests
{
    /// <summary>
    /// Every tier is on the one screen, and the total is the total of everything going. The blanket
    /// question this replaced left out every row §7 asked about separately, so a user who ticked 3 GB of
    /// Tier 1 and 9 GB of Tier 2 read "Total 3 GB" and pressed Clean.
    /// </summary>
    [Fact]
    public void ListsEveryTierAndTotalsThemAll()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(9)),
            Selected("Recycle Bin", SafetyTier.UserData, Gigabytes(1)),
        ]);

        Assert.Equal(["npm", "Maven", "Recycle Bin"], confirmation.Items.Select(i => i.ProviderName));
        Assert.Equal(["3.0 GB", "9.0 GB", "1.0 GB"], confirmation.Items.Select(i => i.SizeLabel));
        Assert.Equal("13.0 GB", confirmation.TotalOf(confirmation.Opening));
    }

    [Fact]
    public void GroupsTheRowsByTierInTierOrderAndKeepsTheSelectionOrderWithinEach()
    {
        var confirmation = For(
        [
            Selected("logs", SafetyTier.UserData, Gigabytes(1)),
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1)),
            Selected("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        Assert.Equal(
            [SafetyTier.RegenerableCache, SafetyTier.RegenerableWithCost, SafetyTier.UserData],
            confirmation.Groups.Select(g => g.Tier));
        Assert.Equal(["npm", "NuGet"], confirmation.Groups[0].Items.Select(i => i.ProviderName));
        Assert.Equal(SafetyTier.RegenerableCache.ToDisplayName(), confirmation.Groups[0].Heading);
    }

    /// <summary>
    /// A Tier 1 group says once that its rows rebuild themselves, and its rows say nothing of their own.
    /// Every other row states what it costs, and no other group claims anything for all its rows.
    /// </summary>
    [Fact]
    public void OnlyTier1IsReassuredAndEveryOtherRowStatesItsCost()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1)),
            Selected("logs", SafetyTier.UserData, Gigabytes(1)),
        ]);

        Assert.NotNull(confirmation.Groups[0].Note);
        Assert.All(confirmation.Groups.Skip(1), group => Assert.Null(group.Note));

        Assert.Null(confirmation.Items[0].Consequence);
        Assert.Contains("restoring it costs real time", confirmation.Items[1].Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", confirmation.Items[2].Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the typed phrase off, a Tier 3 row loses its box, never its sentence: §7 leaves how hard the
    /// confirmation is to give to the user, and never left saying what is unrecoverable to anybody.
    /// </summary>
    [Fact]
    public void ATier3RowWithTheTypedPhraseOffStillSaysItIsPermanent()
    {
        var confirmation = For([Selected("Recycle Bin", SafetyTier.UserData, Gigabytes(9))], requireTypedPhrase: false);

        var item = Assert.Single(confirmation.Items);
        Assert.Null(item.Typing);
        Assert.Contains("cannot be undone", item.Consequence, StringComparison.Ordinal);
        Assert.True(confirmation.IsSatisfiedBy(confirmation.Opening));
    }

    [Fact]
    public void AsksForAnAcknowledgementOnlyOfTier2AndNamesTheRowInIt()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1)),
            Selected("logs", SafetyTier.UserData, Gigabytes(1)),
        ]);

        Assert.Null(confirmation.Items[0].Acknowledgement);
        Assert.Contains("Maven", confirmation.Items[1].Acknowledgement, StringComparison.Ordinal);
        Assert.Null(confirmation.Items[2].Acknowledgement);
        Assert.Equal([null, null, "logs"], confirmation.Items.Select(i => i.Typing?.Phrase));
    }

    /// <summary>Each Tier 2 row needs its own acknowledgement: one ticked box does not stand for another.</summary>
    [Fact]
    public void IsNotSatisfiedUntilEveryTier2RowIsAcknowledged()
    {
        var confirmation = For(
        [
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1)),
            Selected("Gradle", SafetyTier.RegenerableWithCost, Gigabytes(1)),
        ]);

        Assert.False(confirmation.IsSatisfiedBy(confirmation.Opening));
        Assert.False(confirmation.IsSatisfiedBy([Reply("maven", acknowledged: true), Reply("gradle")]));
        Assert.True(confirmation.IsSatisfiedBy([Reply("maven", acknowledged: true), Reply("gradle", acknowledged: true)]));
    }

    /// <summary>
    /// Each Tier 3 row's box must hold that row's name. Trimmed and in any case, because the phrase is a
    /// deliberation device rather than a password, and a name typed into the wrong row's box is no answer.
    /// </summary>
    [Fact]
    public void IsNotSatisfiedUntilEveryTypedPhraseMatchesItsRow()
    {
        var confirmation = For(
        [
            Selected("logs", SafetyTier.UserData, Gigabytes(1)),
            Selected("history", SafetyTier.UserData, Gigabytes(1)),
        ]);

        Assert.False(confirmation.IsSatisfiedBy([Reply("logs", typed: "logs"), Reply("history", typed: "logs")]));
        Assert.False(confirmation.IsSatisfiedBy([Reply("logs", typed: "logs"), Reply("history")]));
        Assert.True(confirmation.IsSatisfiedBy([Reply("logs", typed: " LOGS "), Reply("history", typed: "history")]));
    }

    /// <summary>
    /// A row the user unticks is not going, so it needs no answer and leaves the figure the button
    /// states. That is how a row is left out: Cancel stops the whole clean.
    /// </summary>
    [Fact]
    public void AnUntickedRowNeedsNoAnswerAndLeavesTheTotal()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(9)),
        ]);
        CleanConfirmationReply[] replies = [Reply("npm"), Reply("maven", included: false)];

        Assert.True(confirmation.IsSatisfiedBy(replies));
        Assert.Equal("3.0 GB", confirmation.TotalOf(replies));
        Assert.Equal("Clean 3.0 GB", confirmation.ConfirmLabelOf(replies));
    }

    [Fact]
    public void IsNotSatisfiedWithEveryRowUnticked()
    {
        var confirmation = For([Selected("npm", SafetyTier.RegenerableCache, Gigabytes(3))]);

        Assert.False(confirmation.IsSatisfiedBy([Reply("npm", included: false)]));
    }

    /// <summary>
    /// The button names the verb and the figure. "Delete" is kept for a permanent loss, so it appears
    /// once a Tier 3 row is ticked and goes again when that row is unticked.
    /// </summary>
    [Fact]
    public void TheButtonNamesTheVerbAndTheFigureAndDeletesOnlyAPermanentLoss()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Selected("Recycle Bin", SafetyTier.UserData, Gigabytes(29)),
        ]);

        Assert.Equal("Delete these items?", confirmation.TitleOf(confirmation.Opening));
        Assert.Equal("Delete 32.0 GB", confirmation.ConfirmLabelOf(confirmation.Opening));

        CleanConfirmationReply[] cachesOnly = [Reply("npm"), Reply("recycle bin", included: false)];
        Assert.Equal("Clean these locations?", confirmation.TitleOf(cachesOnly));
        Assert.Equal("Clean 3.0 GB", confirmation.ConfirmLabelOf(cachesOnly));
    }

    /// <summary>A clean that only releases local copies of cloud files says so, and never that they cannot be undone.</summary>
    [Fact]
    public void AReleaseIsAskedAsAReleaseAndClaimsNothingIsLost()
    {
        var release = new ReleaseLocalCopiesStep(@"C:\Users\testuser\OneDrive", "OneDrive", "Local copies") { Estimated = Gigabytes(2) };
        var plan = Plan("OneDrive", SafetyTier.RegenerableWithCost, Gigabytes(2)) with { Steps = [release] };
        var confirmation = For([new Finding(new FakeCleanupProvider("onedrive", SafetyTier.RegenerableWithCost), true, plan)]);

        Assert.Equal(ConfirmationVerb.Release, confirmation.VerbOf(confirmation.Opening));
        Assert.Equal("Release 2.0 GB", confirmation.ConfirmLabelOf(confirmation.Opening));
        Assert.False(confirmation.RemovesFiles);
    }

    /// <summary>
    /// A forecast is qualified on the row and in the total, since a total is only as exact as its least
    /// exact part.
    /// </summary>
    [Fact]
    public void KeepsTheQualifierOnAMeasurementThatCouldNotBeExact()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, ScanSize.Approximate(3 * BytesPerGigabyte)),
            Selected("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        Assert.Equal(["about 3.0 GB", "1.0 GB"], confirmation.Items.Select(i => i.SizeLabel));
        Assert.Equal("about 4.0 GB", confirmation.TotalOf(confirmation.Opening));
    }

    /// <summary>
    /// The answers authorise the ticked rows in the order selected and carry what the planner checks
    /// again, and that check passes. An unticked row is counted as declined, so the run can tell a
    /// clean the user narrowed from one with nothing to remove.
    /// </summary>
    [Fact]
    public void AuthorisesTheTickedRowsWithAnswersThePlannerAccepts()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1)),
            Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1)),
            Selected("logs", SafetyTier.UserData, Gigabytes(1)),
            Selected("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        var (authorised, confirmations, declined) = confirmation.Authorise(
            [Reply("npm"), Reply("maven", acknowledged: true), Reply("logs", typed: "logs"), Reply("nuget", included: false)]);

        Assert.Equal(["npm", "maven", "logs"], authorised.Select(f => f.Provider.Id));
        Assert.Equal(["maven", "logs"], confirmations.Select(c => c.ProviderId));
        Assert.Equal(1, declined);
        ConfirmationRequirement.DemandFor(authorised.Select(f => f.Plan!), confirmations, requireTypedPhrase: true);
    }

    /// <summary>The shell enables its button only on a satisfied answer, so reaching here is a bug, and it fails closed.</summary>
    [Fact]
    public void RefusesToAuthoriseATickedRowItsAnswerDoesNotSatisfy()
    {
        var confirmation = For([Selected("Maven", SafetyTier.RegenerableWithCost, Gigabytes(1))]);

        var refused = Assert.Throws<ConfirmationRequiredException>(() => confirmation.Authorise(confirmation.Opening));

        Assert.Equal("maven", refused.Requirement.ProviderId);
    }

    /// <summary>A row the shell sent no reply for is left out rather than taken, so a shell that forgets a row fails closed.</summary>
    [Fact]
    public void ARowWithNoReplyIsLeftOut()
    {
        var confirmation = For(
        [
            Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1)),
            Selected("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        var (authorised, _, declined) = confirmation.Authorise([Reply("npm")]);

        Assert.Equal(["npm"], authorised.Select(f => f.Provider.Id));
        Assert.Equal(1, declined);
    }

    /// <summary>
    /// A plan that destroys nothing is neither listed nor counted as declined: nothing was put to the
    /// user, so "no selected item was confirmed" would describe the wrong event.
    /// </summary>
    [Fact]
    public void ARowWithNothingToRemoveIsNeitherListedNorCounted()
    {
        var empty = Selected("sdk", SafetyTier.RegenerableWithCost, Gigabytes(1));
        var confirmation = For(
        [
            empty with { Plan = empty.Plan! with { Steps = [] } },
            new Finding(new FakeCleanupProvider("gone"), IsPresent: false, Plan: null),
        ]);

        Assert.Empty(confirmation.Items);
        Assert.Equal(0, confirmation.Refused);
        Assert.False(confirmation.MustAsk(confirmBeforeCleaning: true));
        Assert.Equal(0, confirmation.Authorise(confirmation.Opening).Declined);
    }

    /// <summary>
    /// A plan the planner runs only to verify what it withheld destroys nothing either, so it is not
    /// listed, and it does not make a selection that needs no answer ask one.
    /// </summary>
    [Fact]
    public void AStepFreePlanWithSomethingToProveIsNeitherListedNorAsked()
    {
        var proving = Selected("sdk", SafetyTier.RegenerableWithCost, Gigabytes(1));
        var plan = proving.Plan! with
        {
            Steps = [],
            ProtectedPaths =
            [
                new ProtectedPath(
                    @"C:\Users\testuser\.cache\sdk\withheld",
                    "Withheld.",
                    PresenceBefore: PathPresence.Present,
                    Withheld: Withholding.TooRecent),
            ],
        };
        var confirmation = For([proving with { Plan = plan }]);

        Assert.True(plan.HasSomethingToProve);
        Assert.Empty(confirmation.Items);
        Assert.False(confirmation.MustAsk(confirmBeforeCleaning: true));
    }

    /// <summary>
    /// No answer authorises Tier 4, so it is never listed, whatever the preferences say, and it counts
    /// as declined: something was selected and did not go.
    /// </summary>
    [Fact]
    public void DoNotTouchIsNeverListedAndCountsAsRefused()
    {
        var confirmation = For([Selected("config", SafetyTier.DoNotTouch, Gigabytes(1))], requireTypedPhrase: false);

        Assert.Empty(confirmation.Items);
        Assert.Equal(1, confirmation.Refused);

        var (authorised, confirmations, declined) = confirmation.Authorise(confirmation.Opening);
        Assert.Empty(authorised);
        Assert.Empty(confirmations);
        Assert.Equal(1, declined);
    }

    /// <summary>
    /// The question is always put where a row needs an answer of its own, because §7 makes those answers
    /// conditions of deleting. Only a selection that needs none follows the user's choice to confirm.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableCache, true, false)]
    [InlineData(SafetyTier.RegenerableWithCost, true, true)]
    [InlineData(SafetyTier.UserData, true, true)]
    [InlineData(SafetyTier.UserData, false, false)]
    public void AsksWhereARowNeedsAnAnswerWhateverTheUserChoseAboutConfirming(
        SafetyTier tier, bool requireTypedPhrase, bool asksUnbidden)
    {
        var confirmation = For([Selected("row", tier, Gigabytes(1))], requireTypedPhrase);

        Assert.True(confirmation.MustAsk(confirmBeforeCleaning: true));
        Assert.Equal(asksUnbidden, confirmation.MustAsk(confirmBeforeCleaning: false));
    }

    /// <summary>
    /// The stored "When complete" choice can be days old, so the question says what follows the clean,
    /// and says nothing where nothing does.
    /// </summary>
    [Fact]
    public void SaysWhatFollowsTheCleanWhereAnythingDoes()
    {
        Finding[] selection = [Selected("npm", SafetyTier.RegenerableCache, Gigabytes(1))];

        Assert.Null(CleanConfirmation.For(selection, requireTypedPhrase: true, CompletionAction.Nothing).Then);

        var then = CleanConfirmation.For(selection, requireTypedPhrase: true, CompletionAction.ShutDown).Then;
        Assert.Contains("shut down this PC", then, StringComparison.Ordinal);
        Assert.Contains($"{CompletionCountdown.Seconds}-second countdown", then, StringComparison.Ordinal);
    }

    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    private static CleanConfirmation For(IReadOnlyList<Finding> selection, bool requireTypedPhrase = true) =>
        CleanConfirmation.For(selection, requireTypedPhrase, CompletionAction.Nothing);

    private static CleanConfirmationReply Reply(
        string id, bool included = true, bool acknowledged = false, string? typed = null) =>
        new(id, included, acknowledged, typed);

    private static ScanSize Gigabytes(long count) => new(count * BytesPerGigabyte, count * BytesPerGigabyte);

    private static Finding Selected(string name, SafetyTier tier, ScanSize size) =>
        new(new FakeCleanupProvider(name.ToLowerInvariant(), tier) { Name = name }, IsPresent: true, Plan(name, tier, size));

    private static CleanupPlan Plan(string name, SafetyTier tier, ScanSize size) => new()
    {
        ProviderId = name.ToLowerInvariant(),
        ProviderName = name,
        Tier = tier,
        WhatHappensOnNextUse = "Rebuilt on next use.",
        Steps = [new DeleteDirectoryStep($@"C:\Users\testuser\.cache\{name}", "the cache") { Estimated = size }],
    };
}
