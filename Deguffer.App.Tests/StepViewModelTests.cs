using Deguffer.App.ViewModels;

namespace Deguffer.App.Tests;

/// <summary>
/// How a step's checkbox holds <see cref="Core.Choosing.StepChoice"/>'s answers: the rights it was
/// handed rather than the test host's own, and a keep that moves the checkbox with it.
/// </summary>
public class StepViewModelTests
{
    [Fact]
    public void AStepWithSomethingToRemoveTakesItsRowsTick() =>
        Assert.True(new StepViewModel(Rows.Folder("a", 10), preSelect: true, isKept: false, isElevated: false).IsSelected);

    [Fact]
    public void AKeptStepStartsUntickedWhateverItsRowSays()
    {
        var step = new StepViewModel(Rows.Folder("a", 10), preSelect: true, isKept: true, isElevated: false);

        Assert.False(step.IsSelected);
        Assert.False(step.CanBeSelected);
    }

    /// <summary>
    /// The rights are the ones the page handed down. Read from the process token instead, both halves
    /// of this would depend on how the test runner was launched, and one of them could never run.
    /// </summary>
    [Fact]
    public void AStepNeedingRightsFollowsTheRightsItWasGiven()
    {
        var unelevated = new StepViewModel(Rows.Folder("a", 10, requiresElevation: true), preSelect: true, isKept: false, isElevated: false);
        var elevated = new StepViewModel(Rows.Folder("a", 10, requiresElevation: true), preSelect: true, isKept: false, isElevated: true);

        Assert.True(unelevated.NeedsElevationFirst);
        Assert.False(unelevated.CanBeSelected);
        Assert.False(unelevated.IsSelected);

        Assert.False(elevated.NeedsElevationFirst);
        Assert.True(elevated.CanBeSelected);
        Assert.True(elevated.IsSelected);
    }

    /// <summary>A keep made from the item list has to disable the checkbox and relabel the button beside it.</summary>
    [Fact]
    public void KeepingAStepAnnouncesEverythingThatReadsIt()
    {
        var step = new StepViewModel(Rows.Folder("a", 10, key: "a"), preSelect: false, isKept: false, isElevated: false);
        var raised = step.Notifications();

        step.IsKept = true;

        Assert.False(step.CanBeSelected);
        Assert.Equal("Stop keeping", step.KeepActionLabel);
        Assert.Contains(nameof(StepViewModel.CanBeSelected), raised);
        Assert.Contains(nameof(StepViewModel.KeepActionLabel), raised);
        Assert.Contains(nameof(StepViewModel.KeepActionName), raised);
    }
}
