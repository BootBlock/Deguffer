using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a step of the preview can be ticked, and whether it starts ticked. The rights the process
/// holds are an argument, so both answers are asserted here for both tokens, whichever one the test
/// host happens to run under.
/// </summary>
public sealed class StepChoiceTests
{
    private static DeleteDirectoryStep Folder(long bytes, bool requiresElevation = false) =>
        new(@"C:\Users\testuser\AppData\Local\Fake\Cache", "Cached files")
        {
            Estimated = ScanSize.FromLengths(bytes),
            RequiresElevation = requiresElevation,
        };

    private static Finding Offering(params CleanupStep[] steps) =>
        new(new FakeCleanupProvider("fake"), IsPresent: true, new CleanupPlan
        {
            ProviderId = "fake",
            ProviderName = "Fake",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Rebuilt.",
            Steps = steps,
        });

    [Fact]
    public void AStepWithSomethingToRemoveCanBeTicked() =>
        Assert.True(StepChoice.CanBeSelected(Folder(10), isKept: false, isElevated: false));

    [Fact]
    public void AStepWithNothingToRemoveCannotBeTicked() =>
        Assert.False(StepChoice.CanBeSelected(Folder(0), isKept: false, isElevated: true));

    /// <summary>
    /// A leftover of empty folders frees no bytes and still removes something, so it can be chosen.
    /// A byte test in place of <see cref="CleanupStep.RemovesSomething"/> would refuse it.
    /// </summary>
    [Fact]
    public void ALeftoverOfEmptyFoldersCanBeTicked()
    {
        var leftover = Folder(0) with { IsLeftover = true, Estimated = new ScanSize(0, 0, Entries: 3) };

        Assert.True(StepChoice.CanBeSelected(leftover, isKept: false, isElevated: false));
    }

    /// <summary>A kept item is not in its row's plan, so a tick on it would be counted and remove nothing.</summary>
    [Fact]
    public void AKeptStepCannotBeTicked() =>
        Assert.False(StepChoice.CanBeSelected(Folder(10), isKept: true, isElevated: true));

    [Fact]
    public void AStepNeedingRightsThisProcessLacksCannotBeTicked()
    {
        var step = Folder(10, requiresElevation: true);

        Assert.True(StepChoice.NeedsElevationFirst(step, isElevated: false));
        Assert.False(StepChoice.CanBeSelected(step, isKept: false, isElevated: false));
    }

    [Fact]
    public void AStepNeedingRightsThisProcessHoldsCanBeTicked()
    {
        var step = Folder(10, requiresElevation: true);

        Assert.False(StepChoice.NeedsElevationFirst(step, isElevated: true));
        Assert.True(StepChoice.CanBeSelected(step, isKept: false, isElevated: true));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AStepThatCanBeTickedStartsAsItsRowSays(bool rowSays, bool expected) =>
        Assert.Equal(expected, StepChoice.StartsSelected(Folder(10), rowSays, isKept: false, isElevated: false));

    /// <summary>
    /// A disabled checkbox that starts ticked is a selection the user has no way to clear, so each
    /// reason a step cannot be ticked also refuses the row's tick.
    /// </summary>
    [Fact]
    public void AStepThatCannotBeTickedNeverStartsTickedWhateverItsRowSays()
    {
        Assert.False(StepChoice.StartsSelected(Folder(10), rowSays: true, isKept: true, isElevated: true));
        Assert.False(StepChoice.StartsSelected(Folder(0), rowSays: true, isKept: false, isElevated: true));
        Assert.False(StepChoice.StartsSelected(
            Folder(10, requiresElevation: true), rowSays: true, isKept: false, isElevated: false));
    }

    [Fact]
    public void ARowCanBeTickedWhereOneOfItsStepsCan() =>
        Assert.True(StepChoice.AnyCanBeSelected(
            Offering(Folder(10, requiresElevation: true), Folder(5)), isElevated: false));

    /// <summary>
    /// The Windows servicing logs: real space, every step needing administrator rights. Ticking the
    /// row would tick nothing and leave a row saying it is selected that removes nothing.
    /// </summary>
    [Fact]
    public void ARowWhoseEveryStepNeedsRightsCannotBeTickedUnelevated()
    {
        var finding = Offering(Folder(10, requiresElevation: true), Folder(20, requiresElevation: true));

        Assert.False(StepChoice.AnyCanBeSelected(finding, isElevated: false));
        Assert.True(StepChoice.AnyCanBeSelected(finding, isElevated: true));
    }

    [Fact]
    public void ARowWithNoPlanCannotBeTicked() =>
        Assert.False(StepChoice.AnyCanBeSelected(
            new Finding(new FakeCleanupProvider("fake"), IsPresent: false, Plan: null), isElevated: true));
}
