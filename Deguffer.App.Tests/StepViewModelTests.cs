using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.App.Tests;

public class StepViewModelTests
{
    private static DeleteDirectoryStep Folder(long bytes) =>
        new(@"C:\Users\testuser\src\app\node_modules", "Installed npm packages")
        {
            Estimated = ScanSize.FromLengths(bytes),
        };

    [Fact]
    public void AStepWithSomethingToRemoveTakesItsRowsTick()
    {
        Assert.True(new StepViewModel(Folder(10), preSelect: true, isKept: false).IsSelected);
    }

    [Fact]
    public void AKeptStepStartsUntickedWhateverItsRowSays()
    {
        Assert.False(new StepViewModel(Folder(10), preSelect: true, isKept: true).IsSelected);
    }
}
