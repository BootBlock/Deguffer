using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// A key or a click runs a view-model's command only where the command will run, and says so, so a key
/// that does not apply goes on to the control underneath. A toolkit command runs when it is told to
/// whatever it would answer, so the shell asks first.
/// </summary>
public sealed class CommandsTests
{
    [Fact]
    public void ACommandThatWillRunRunsAndSaysSo()
    {
        var ran = 0;
        var command = new RelayCommand(() => ran++, () => true);

        Assert.True(command.TryExecute());
        Assert.Equal(1, ran);
    }

    [Fact]
    public void ACommandThatWillNotRunDoesNotAndSaysSo()
    {
        var ran = 0;
        var command = new RelayCommand(() => ran++, () => false);

        Assert.False(command.TryExecute());
        Assert.Equal(0, ran);
    }

    [Fact]
    public void TheParameterIsAskedAboutAndRunWith()
    {
        var ran = new List<string?>();
        var command = new RelayCommand<string>(ran.Add, chosen => chosen == "this");

        Assert.False(command.TryExecute("that"));
        Assert.True(command.TryExecute("this"));
        Assert.Equal(["this"], ran);
    }
}
