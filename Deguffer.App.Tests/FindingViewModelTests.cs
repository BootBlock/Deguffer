using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How one Storage row holds its steps together: the order it is built in, the roll-up between its
/// checkbox and its steps, how many events one thing the user did raises, and how it follows a keep
/// list or a re-plan that changed under it. What each step may do is Core's; see
/// <see cref="Core.Choosing.StepChoice"/>.
/// </summary>
public class FindingViewModelTests
{
    private static readonly FakeCleanupProvider Cache = new("cache");

    private static readonly FakeCleanupProvider Downloads = new("downloads", SafetyTier.RegenerableWithCost);

    private static KeepList Keeping(params string[] keys) =>
        keys.Aggregate(KeepList.Empty, (list, key) => list.With(new KeptItem(Cache.Id, Cache.Name, new ItemIdentity(key, key))));

    /// <summary>
    /// A row whose steps start ticked is built whole. The row used to subscribe to each step as the
    /// step was made, so the first ticked step reported its change before the row had a list of steps
    /// to roll it up over. Built inside a progress callback, the exception reached nobody and the row
    /// never appeared.
    /// </summary>
    [Fact]
    public void ARowWhoseStepsStartTickedIsBuiltWhole()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));

        Assert.Equal(2, row.Steps.Count);
        Assert.All(row.Steps, step => Assert.True(step.IsSelected));
        Assert.True(row.IsSelected);
        Assert.Equal(30, row.SelectedSize.Reclaimable);
    }

    /// <summary>
    /// The shell's half of the researched rule: the row reads its default and its chip from the
    /// provider's basis. Tier 1 here, so only the basis can be what leaves it unticked.
    /// </summary>
    [Fact]
    public void AResearchedRowStartsUntickedAndSaysItIsResearched()
    {
        var researched = new FakeCleanupProvider("researched") { Basis = ProviderBasis.Researched };

        var row = Rows.Row(Rows.Found(researched, Rows.Folder("a", 10)));

        Assert.False(row.IsSelected);
        Assert.False(Assert.Single(row.Steps).IsSelected);
        Assert.True(row.Facts.IsResearched);
        Assert.Equal("Researched", row.Facts.BasisLabel);
        Assert.False(Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10))).Facts.IsResearched);
    }

    /// <summary>
    /// A row is selected when a step in it is, whatever §3's default or the memory says. A row whose
    /// every step needs rights this process lacks would otherwise render ticked and disabled, and
    /// enable Clean against nothing.
    /// </summary>
    [Fact]
    public void ARowWhoseStepsCannotBeTickedStartsUntickedWhateverItsDefault()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, requiresElevation: true)), isElevated: false);

        Assert.False(row.IsSelected);
        Assert.False(row.CanBeSelected);
        Assert.Equal(FindingStatus.NeedsElevation, row.Status);
    }

    [Fact]
    public void TheRowsStatusFollowsTheRightsItWasGiven()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, requiresElevation: true)), isElevated: true);

        Assert.True(row.IsSelected);
        Assert.True(row.CanBeSelected);
        Assert.Equal(FindingStatus.ReadyToClean, row.Status);
    }

    /// <summary>
    /// The row's checkbox is a shorthand for every step it can tick. A step it cannot tick is passed
    /// over, and the whole click is one event: each event re-totals the page and writes the remembered
    /// selection to disk.
    /// </summary>
    [Fact]
    public void TickingTheRowTicksEveryStepThatCanBeTickedAsOneChange()
    {
        var row = Rows.Row(Rows.Found(
            Downloads, Rows.Folder("a", 10), Rows.Folder("b", 20, requiresElevation: true), Rows.Folder("c", 0)));
        var events = row.SelectionEvents();

        row.ToggleCommand.Execute(null);

        Assert.Equal([true, false, false], row.Steps.Select(step => step.IsSelected));
        Assert.Single(events);
        Assert.Equal(10, row.SelectedSize.Reclaimable);
    }

    [Fact]
    public void UntickingTheRowClearsEveryStepAsOneChange()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));
        var events = row.SelectionEvents();

        row.ToggleCommand.Execute(null);

        Assert.All(row.Steps, step => Assert.False(step.IsSelected));
        Assert.Single(events);
    }

    /// <summary>Unticking the last step clears the row, rather than leaving a ticked row that removes nothing.</summary>
    [Fact]
    public void UntickingTheLastStepClearsTheRow()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10)));
        var events = row.SelectionEvents();

        row.Steps[0].IsSelected = false;

        Assert.False(row.IsSelected);
        Assert.Single(events);
    }

    /// <summary>
    /// Unticking one workspace of several is the ordinary case for per-item selection: the row stays
    /// ticked, and the page still hears about it, because its total moved.
    /// </summary>
    [Fact]
    public void UntickingOneStepOfSeveralLeavesTheRowTickedAndIsHeard()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));
        var events = row.SelectionEvents();
        var raised = row.Notifications();

        row.Steps[0].IsSelected = false;

        Assert.True(row.IsSelected);
        Assert.Single(events);
        Assert.Contains(nameof(FindingViewModel.SelectedSize), raised);
        Assert.Equal(20, row.SelectedSize.Reclaimable);
    }

    /// <summary>
    /// A click on a group of the item list writes every step in it. Raised per step, one click on a
    /// thousand items was a thousand re-totals of the page and a thousand writes to disk.
    /// </summary>
    [Fact]
    public void TickingManyStepsAtOnceIsOneChange()
    {
        var row = Rows.Row(Rows.Found(
            Downloads, [.. Enumerable.Range(0, 100).Select(i => Rows.Folder($"w{i}", 10))]));
        var events = row.SelectionEvents();
        var raised = row.Notifications();

        row.SetSelected(row.Steps, true);

        Assert.All(row.Steps, step => Assert.True(step.IsSelected));
        Assert.True(row.IsSelected);
        Assert.Single(events);
        Assert.Single(raised, name => name == nameof(FindingViewModel.SelectedSize));
    }

    [Fact]
    public void TickingSeveralStepsPassesOverOnesThatCannotBeTicked()
    {
        var row = Rows.Row(Rows.Found(Downloads, Rows.Folder("a", 10), Rows.Folder("b", 20, requiresElevation: true)));

        row.SetSelected(row.Steps, true);

        Assert.Equal([true, false], row.Steps.Select(step => step.IsSelected));
    }

    /// <summary>
    /// A row with some of its items ticked shows a mixed checkbox and the size that will go against the
    /// whole, so the list never shows a full tick beside "18.4 GB" for a clean that takes 300 MB.
    /// </summary>
    [Fact]
    public void ARowWithSomeItemsTickedIsMixedAndStatesWhatIsChosen()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 1024), Rows.Folder("b", 2048), Rows.Folder("c", 4096)));

        row.Steps[1].IsSelected = false;

        Assert.Null(row.SelectionState);
        Assert.True(row.IsSelected);
        Assert.Equal($"{FreeSpace.Format(5120)} of {FreeSpace.Format(7168)}", row.SizeLabel);
        Assert.Equal("2 of 3 items", row.ItemsLinkLabel);
    }

    /// <summary>A row ticked or cleared whole states the whole, and its checkbox is not mixed.</summary>
    [Fact]
    public void ARowTickedOrClearedWholeStatesTheWhole()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 1024), Rows.Folder("b", 2048)));

        Assert.True(row.SelectionState);
        Assert.Equal(FreeSpace.Format(3072), row.SizeLabel);
        Assert.Equal("2 items", row.ItemsLinkLabel);

        row.SetTicked(false);

        Assert.False(row.SelectionState);
        Assert.Equal(FreeSpace.Format(3072), row.SizeLabel);
        Assert.Equal("2 items", row.ItemsLinkLabel);
    }

    /// <summary>
    /// A step nobody can tick does not hold the checkbox short of full: its own checkbox is disabled, so
    /// a row that counted it could never be ticked by any click. It still counts towards what the row
    /// holds, so the figure states the part a clean takes against that, and the link the items ticked.
    /// </summary>
    [Fact]
    public void AStepThatCannotBeTickedLeavesTheBoxFullAndTheFigureAPart()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 1024), Rows.Folder("b", 8192, requiresElevation: true)));

        Assert.True(row.SelectionState);
        Assert.Equal($"{FreeSpace.Format(1024)} of {FreeSpace.Format(9216)}", row.SizeLabel);
        Assert.Equal("1 of 2 items", row.ItemsLinkLabel);
    }

    /// <summary>
    /// A row nothing in which can be ticked states what it holds, which is the figure that decides
    /// whether reopening as administrator is worth it.
    /// </summary>
    [Fact]
    public void ARowNeedingRightsForEveryStepStatesItsWhole()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 8192, requiresElevation: true)));

        Assert.Equal(FreeSpace.Format(8192), row.SizeLabel);
    }

    /// <summary>
    /// A click on a mixed row clears it, as a group heading in the item list does. Ticking the rest
    /// would add items to the run the user may never have looked at.
    /// </summary>
    [Fact]
    public void ClickingAMixedRowClearsItAsOneChange()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)));
        row.Steps[0].IsSelected = false;
        var events = row.SelectionEvents();

        row.ToggleCommand.Execute(null);

        Assert.All(row.Steps, step => Assert.False(step.IsSelected));
        Assert.False(row.SelectionState);
        Assert.False(row.IsSelected);
        Assert.Single(events);
    }

    /// <summary>
    /// A tick that leaves a row as mixed as it was changes neither of its states, and still moves the
    /// figures on it, which are announced with the size rather than with the states.
    /// </summary>
    [Fact]
    public void ATickThatLeavesTheRowMixedStillMovesItsFigures()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20), Rows.Folder("c", 40)));
        row.Steps[0].IsSelected = false;
        var raised = row.Notifications();

        row.Steps[1].IsSelected = false;

        Assert.Null(row.SelectionState);
        Assert.Contains(nameof(FindingViewModel.SizeLabel), raised);
        Assert.Contains(nameof(FindingViewModel.ItemsLinkLabel), raised);
        Assert.DoesNotContain(nameof(FindingViewModel.SelectionState), raised);
    }

    /// <summary>
    /// The ceiling the info bar states beside the selected total counts only what a tick can reach:
    /// not a step needing rights this process lacks, and not a kept item.
    /// </summary>
    [Fact]
    public void WhatCanBeSelectedCountsOnlyStepsThatCanBeTicked()
    {
        var row = Rows.Row(
            Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20, requiresElevation: true), Rows.Folder("c", 40, key: "c")),
            keeps: Keeping("c"));

        Assert.Equal([10L], row.SelectableSteps.Select(step => step.Step.Reclaim.Reclaimable));
        Assert.Equal(10, row.SelectedSize.Reclaimable);
    }

    /// <summary>
    /// Keeping an item takes it out of the row's plan and unticks it, and is one change to the page.
    /// The item stays listed, so it is released from the same place it was kept.
    /// </summary>
    [Fact]
    public void KeepingAnItemUnticksItAndTakesItOutOfTheRow()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, key: "a"), Rows.Folder("b", 20, key: "b")));
        var events = row.SelectionEvents();

        row.ApplyKeepList(Keeping("a"));

        var kept = row.Steps[0];
        Assert.True(kept.IsKept);
        Assert.False(kept.IsSelected);
        Assert.Equal(2, row.Steps.Count);
        Assert.Equal(["b"], row.Finding.Plan!.Steps.Select(step => step.Identity!.Key));
        Assert.Equal(20, row.SelectedSize.Reclaimable);
        Assert.Single(events);
    }

    /// <summary>Keeping it was the user's last word about it, so a released item comes back unticked.</summary>
    [Fact]
    public void AReleasedItemComesBackUnticked()
    {
        var row = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, key: "a"), Rows.Folder("b", 20, key: "b")), keeps: Keeping("a"));
        var events = row.SelectionEvents();

        row.ApplyKeepList(KeepList.Empty);

        var released = row.Steps[0];
        Assert.False(released.IsKept);
        Assert.True(released.CanBeSelected);
        Assert.False(released.IsSelected);
        Assert.Equal(2, row.Finding.Plan!.Steps.Count);
        Assert.Single(events);
    }

    /// <summary>
    /// Every row is brought up to date whenever the keep list changes, and most have nothing to change.
    /// Those raise nothing: an event from each would re-total the page and write the selection to disk
    /// once per row.
    /// </summary>
    [Fact]
    public void AKeepListThatChangesNothingInTheRowRaisesNothing()
    {
        var untouched = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, key: "a")));
        var alreadyKept = Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10, key: "a"), Rows.Folder("b", 20, key: "b")), keeps: Keeping("a"));
        var events = untouched.SelectionEvents();
        var keptEvents = alreadyKept.SelectionEvents();
        var raised = alreadyKept.Notifications();

        untouched.ApplyKeepList(KeepList.Empty);
        alreadyKept.ApplyKeepList(Keeping("a"));

        Assert.Empty(events);
        Assert.Empty(keptEvents);
        Assert.Empty(raised);
    }

    /// <summary>
    /// A re-plan writes over the row where it stands, and every surviving step comes back as the user
    /// left it. It raises no selection change: the page re-totals once the row has landed.
    /// </summary>
    [Fact]
    public void AReplanWritesOverTheRowAsTheUserLeftIt()
    {
        var memory = Rows.NothingRemembered();
        var row = new FindingViewModel(
            Rows.Found(Cache, Rows.Folder("a", 10), Rows.Folder("b", 20)), memory, KeepList.Empty, isElevated: false);

        row.Steps[0].IsSelected = false;
        memory.Remember(Cache.Id, row.ToRemembered());

        var oldStep = row.Steps[0];
        var events = row.SelectionEvents();
        var raised = row.Notifications();

        row.Replan(Rows.Found(Cache, Rows.Folder("a", 5), Rows.Folder("b", 15), Rows.Folder("c", 1)), memory, KeepList.Empty);

        Assert.Equal([false, true, true], row.Steps.Select(step => step.IsSelected));
        Assert.Equal(16, row.SelectedSize.Reclaimable);
        Assert.Empty(events);
        Assert.Contains(string.Empty, raised);

        // The steps it replaced are no longer part of the row. A tick on one left behind in a dialog
        // must not reach the row, or it would move a total that no run reads.
        oldStep.IsSelected = true;
        Assert.Empty(events);
        Assert.False(row.Steps[0].IsSelected);
    }

    /// <summary>A step the run emptied comes back unticked, because it can no longer be ticked.</summary>
    [Fact]
    public void AReplanUnticksAStepTheRunEmptied()
    {
        var memory = Rows.NothingRemembered();
        var row = new FindingViewModel(Rows.Found(Cache, Rows.Folder("a", 10)), memory, KeepList.Empty, isElevated: false);
        memory.Remember(Cache.Id, row.ToRemembered());

        row.Replan(Rows.Found(Cache, Rows.Folder("a", 0)), memory, KeepList.Empty);

        Assert.False(row.Steps[0].IsSelected);
        Assert.False(row.IsSelected);
    }

    /// <summary>
    /// The Contents tab states the one step of a row with nothing to choose between. A row whose steps
    /// are listed as items, or that has more than one, has no sole step.
    /// </summary>
    [Fact]
    public void OnlyARowWithOneStepAndNoItemListHasASoleStep()
    {
        var parts = new FakeCleanupProvider("parts") { Grain = StepGrain.Parts };

        Assert.NotNull(Rows.Row(Rows.Found(parts, Rows.Folder("a", 10))).SoleStep);
        Assert.Null(Rows.Row(Rows.Found(parts, Rows.Folder("a", 10), Rows.Folder("b", 10))).SoleStep);
        Assert.Null(Rows.Row(Rows.Found(Cache, Rows.Folder("a", 10))).SoleStep);
    }
}
