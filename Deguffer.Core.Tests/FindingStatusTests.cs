using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The words a preview row states beside its size.
///
/// They live with the status value rather than in the view-model so that the row and the sentence
/// above it are read off one answer — see <see cref="PreviewSummaryTests"/> for what went wrong
/// while they were two.
/// </summary>
public sealed class FindingStatusTests
{
    /// <summary>
    /// The dangerous direction: exactly one state may claim the folder is clear. The seven
    /// neighbouring states measure zero as well, and telling the user a folder is clear when
    /// Deguffer never read it is the whole of issue #38 — or when Windows would not let it take what
    /// is there, which is issue #117.
    /// </summary>
    [Theory]
    [InlineData(FindingStatus.AwaitingSourceFolders)]
    [InlineData(FindingStatus.ToolchainMissing)]
    [InlineData(FindingStatus.UnreadableRoot)]
    [InlineData(FindingStatus.NotExamined)]
    [InlineData(FindingStatus.RecentContentHeldBack)]
    [InlineData(FindingStatus.RefusedByWindows)]
    [InlineData(FindingStatus.OnKeepList)]
    [InlineData(FindingStatus.MailStoresHeldBack)]
    [InlineData(FindingStatus.UpdateInProgress)]
    [InlineData(FindingStatus.ReadyToClean)]
    [InlineData(FindingStatus.NeedsElevation)]
    public void OnlyTheClearStateSaysAlreadyClear(FindingStatus status)
    {
        Assert.NotEqual("Already clear", status.ToStatusLabel());
    }

    [Fact]
    public void TheClearStateSaysAlreadyClear()
    {
        Assert.Equal("Already clear", FindingStatus.AlreadyClear.ToStatusLabel());
    }

    /// <summary>
    /// The label is drawn under the size, in the one column the standard row pins, and every other
    /// thing on that row is positioned against that column's left edge. So a label wider than the
    /// column does not merely look cramped: it widens the column on its own row, and the "What is
    /// this?" link on that row alone stops lining up with the rest of the list.
    ///
    /// <para>The column is 104 effective pixels. The row's secondary type measures about 5.3 of
    /// those per character at its widest — "Ready to clean" renders 74 pixels wide and "Not
    /// installed" 65 — which puts the ceiling at twenty characters. Two labels were over it and
    /// pushed their rows' links visibly left.</para>
    /// </summary>
    [Fact]
    public void NoStateSaysMoreThanTheRowsPinnedColumnHolds()
    {
        const int ceiling = 20;

        Assert.All(
            Enum.GetValues<FindingStatus>(),
            status => Assert.InRange(status.ToStatusLabel().Length, 1, ceiling));
    }

    /// <summary>Every state has words of its own, and no two states share them.</summary>
    [Fact]
    public void EveryStateHasDistinctWordsOfItsOwn()
    {
        var labels = Enum.GetValues<FindingStatus>().Select(s => s.ToStatusLabel()).ToList();

        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());
    }

    private static DeleteDirectoryStep Folder(long bytes, bool requiresElevation = false) =>
        new(@"C:\Users\testuser\AppData\Local\Fake\Cache", "Cached files")
        {
            Estimated = ScanSize.FromLengths(bytes),
            RequiresElevation = requiresElevation,
        };

    private static ProtectedPath Withheld(Withholding why) =>
        new(@"C:\Users\testuser\AppData\Local\Fake\Kept", "Withheld.", PathPresence.Present, Withheld: why);

    private static Finding Found(CleanupPlan? plan, bool present = true, bool awaiting = false) =>
        new(new FakeCleanupProvider("fake"), present, plan, awaiting);

    private static CleanupPlan Plan(params CleanupStep[] steps) => new()
    {
        ProviderId = "fake",
        ProviderName = "Fake",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Rebuilt.",
        Steps = steps,
    };

    /// <summary>
    /// Waiting for a folder comes before presence: the .NET build output is present whenever the SDK
    /// is, and with no approved folder it has as little to report as a tool that is absent.
    /// </summary>
    [Fact]
    public void ARowWaitingForAFolderSaysSoWhetherOrNotItsToolIsPresent()
    {
        Assert.Equal(FindingStatus.AwaitingSourceFolders, Found(Plan(), present: true, awaiting: true).ToStatus(false));
        Assert.Equal(FindingStatus.AwaitingSourceFolders, Found(Plan(), present: false, awaiting: true).ToStatus(false));
    }

    [Fact]
    public void AnAbsentToolIsNotInstalled() =>
        Assert.Equal(FindingStatus.ToolchainMissing, Found(null, present: false).ToStatus(isElevated: true));

    /// <summary>
    /// Every one of these measures zero, and each is asked in this order: the first reason that
    /// holds is the one the row states. A plan below carries every later reason as well as its own,
    /// so an order that moved one state past the next fails here.
    /// </summary>
    [Fact]
    public void ARowWithNothingToRemoveStatesTheFirstReasonThatHolds()
    {
        var recent = Folder(0) with { WithheldRecent = true };
        var refused = Folder(0) with { Refused = new Refusals(new RefusalTally(1, 10), default) };
        var everything = Plan(recent, refused) with
        {
            HasUnreadableRoot = true,
            WasNotExamined = true,
            ProtectedPaths =
            [
                Withheld(Withholding.OnKeepList),
                Withheld(Withholding.MailStore),
                Withheld(Withholding.UpdateInProgress),
            ],
        };

        Assert.Equal(FindingStatus.UnreadableRoot, Found(everything).ToStatus(false));
        Assert.Equal(FindingStatus.NotExamined, Found(everything with { HasUnreadableRoot = false }).ToStatus(false));

        var examined = everything with { HasUnreadableRoot = false, WasNotExamined = false };
        Assert.Equal(FindingStatus.RecentContentHeldBack, Found(examined).ToStatus(false));

        var notRecent = examined with { Steps = [refused] };
        Assert.Equal(FindingStatus.RefusedByWindows, Found(notRecent).ToStatus(false));

        var notRefused = notRecent with { Steps = [] };
        Assert.Equal(FindingStatus.OnKeepList, Found(notRefused).ToStatus(false));

        var notKept = notRefused with { ProtectedPaths = [Withheld(Withholding.MailStore), Withheld(Withholding.UpdateInProgress)] };
        Assert.Equal(FindingStatus.MailStoresHeldBack, Found(notKept).ToStatus(false));

        var noMail = notKept with { ProtectedPaths = [Withheld(Withholding.UpdateInProgress)] };
        Assert.Equal(FindingStatus.UpdateInProgress, Found(noMail).ToStatus(false));

        Assert.Equal(FindingStatus.AlreadyClear, Found(Plan()).ToStatus(false));
        Assert.Equal(FindingStatus.AlreadyClear, Found(null).ToStatus(false));
    }

    /// <summary>
    /// Something to remove outranks every reason for having nothing: a row with space and a note
    /// about what was held back is ready, not held back.
    /// </summary>
    [Fact]
    public void ARowWithSomethingToRemoveIsReadyWhateverElseItHolds() =>
        Assert.Equal(
            FindingStatus.ReadyToClean,
            Found(Plan(Folder(10)) with { WasNotExamined = true, ProtectedPaths = [Withheld(Withholding.OnKeepList)] })
                .ToStatus(isElevated: false));

    /// <summary>
    /// "Ready to clean" beside a checkbox that cannot be ticked would contradict itself. The token is an
    /// argument, so the answer for both is asserted whichever one runs this test.
    /// </summary>
    [Fact]
    public void ARowWhoseSpaceAllNeedsRightsIsReadyOnlyForAProcessHoldingThem()
    {
        var finding = Found(Plan(Folder(10, requiresElevation: true)));

        Assert.Equal(FindingStatus.NeedsElevation, finding.ToStatus(isElevated: false));
        Assert.Equal(FindingStatus.ReadyToClean, finding.ToStatus(isElevated: true));
    }

    [Fact]
    public void ARowWithOneStepItCanTakeIsReadyUnelevated() =>
        Assert.Equal(
            FindingStatus.ReadyToClean,
            Found(Plan(Folder(10, requiresElevation: true), Folder(5))).ToStatus(isElevated: false));
}
