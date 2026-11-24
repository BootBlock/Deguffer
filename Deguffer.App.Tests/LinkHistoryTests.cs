using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// The window's Back command is offered after a link and not after a rail click, says so once per
/// change, and asks the window to show the page the link was on. Each test's window records what Back
/// asked it to show, as MainWindow shows it and selects it on the rail. Which page Back leads to is
/// <see cref="Core.Viewing.LinkTrail{T}"/>'s rule, tested in Core.
/// </summary>
public sealed class LinkHistoryTests
{
    private readonly List<Destination> _shown = [];
    private int _changes;

    [Fact]
    public void NothingIsOfferedBeforeALink() =>
        Assert.False(History(Destination.Storage).BackCommand.CanExecute(null));

    [Fact]
    public void BackAfterMemorysLinkShowsMemory()
    {
        var history = History(Destination.Memory);

        history.Follow(Destination.Startup);

        Assert.True(history.BackCommand.CanExecute(null));
        Assert.Equal(1, _changes);

        history.BackCommand.Execute(null);

        Assert.Equal([Destination.Memory], _shown);
        Assert.False(history.BackCommand.CanExecute(null));
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void ARailClickOffersNothing()
    {
        var history = History(Destination.Memory);

        history.Choose(Destination.Startup);

        Assert.False(history.BackCommand.CanExecute(null));
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void ARailClickAfterALinkWithdrawsBack()
    {
        var history = History(Destination.Memory);
        history.Follow(Destination.Startup);

        history.Choose(Destination.Explore);

        Assert.False(history.BackCommand.CanExecute(null));
        Assert.Equal(2, _changes);
        Assert.Empty(_shown);
    }

    /// <summary>A second link leaves Back offered, so the command says nothing new and the button does not flicker.</summary>
    [Fact]
    public void ASecondLinkRaisesNoChange()
    {
        var history = History(Destination.Memory);
        history.Follow(Destination.Startup);

        history.Follow(Destination.Explore);
        Assert.Equal(1, _changes);

        history.BackCommand.Execute(null);

        Assert.Equal([Destination.Startup], _shown);
        Assert.True(history.BackCommand.CanExecute(null));
        Assert.Equal(1, _changes);
    }

    private LinkHistory History(Destination start)
    {
        var history = new LinkHistory(start, _shown.Add);
        history.BackCommand.CanExecuteChanged += (_, _) => _changes++;
        return history;
    }
}
