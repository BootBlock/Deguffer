using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// The window's Back is offered after a link and not after a rail click, and returns to the page the
/// link was on. Each test's window records what Back asked it to show, as MainWindow shows it and
/// selects it on the rail.
/// </summary>
public sealed class LinkHistoryTests
{
    private readonly List<Destination> _shown = [];
    private int _changes;

    [Fact]
    public void NothingIsOfferedBeforeALink() =>
        Assert.False(History(Destination.Storage).BackCommand.CanExecute(null));

    [Fact]
    public void BackAfterMemorysLinkReturnsToMemory()
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

    /// <summary>A link followed from a page a link opened is followed from there, so Back retraces both in turn.</summary>
    [Fact]
    public void LinksFollowedInTurnAreRetracedInTurn()
    {
        var history = History(Destination.Memory);
        history.Follow(Destination.Startup);
        history.Follow(Destination.Explore);

        history.BackCommand.Execute(null);
        history.BackCommand.Execute(null);

        Assert.Equal([Destination.Startup, Destination.Memory], _shown);
    }

    /// <summary>
    /// A link followed from the page Back returned to is followed from there, and not from the page the
    /// window left by going back.
    /// </summary>
    [Fact]
    public void ALinkAfterBackIsFollowedFromWhereBackLed()
    {
        var history = History(Destination.Memory);
        history.Follow(Destination.Startup);
        history.BackCommand.Execute(null);

        history.Follow(Destination.Startup);
        history.BackCommand.Execute(null);

        Assert.Equal([Destination.Memory, Destination.Memory], _shown);
    }

    /// <summary>A rail click moves where the window is, so the next link is followed from the page clicked.</summary>
    [Fact]
    public void ALinkAfterARailClickIsFollowedFromThePageClicked()
    {
        var history = History(Destination.Storage);
        history.Choose(Destination.Memory);

        history.Follow(Destination.Startup);
        history.BackCommand.Execute(null);

        Assert.Equal([Destination.Memory], _shown);
    }

    private LinkHistory History(Destination start)
    {
        var history = new LinkHistory(start, _shown.Add);
        history.BackCommand.CanExecuteChanged += (_, _) => _changes++;
        return history;
    }
}
