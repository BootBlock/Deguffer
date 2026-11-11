using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Network;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the network browser wires its tree: a node lists once, when it is opened, fails on its own
/// line, stops when it is closed, and the added computers and the place it was left at are kept in
/// the preferences. What the tree holds is Core's (<c>NetworkTreeTests</c>).
/// </summary>
public sealed class NetworkBrowserTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeNetwork _network = new();
    private readonly ManualTimeProvider _time = new();
    private readonly PreferenceService _preferences;

    public NetworkBrowserTests()
    {
        _preferences = new PreferenceService(new PreferenceStore(new FakeUserEnvironment(_temp.Path)));
        _network.ConnectedAnswer = NetworkListing<NetworkConnection>.Listed([new NetworkConnection(@"\\nas.test\media", "Z:")]);
        _network
            .Computer("nas.test", new ListedShare("media", ShareKind.Disk, false), new ListedShare("Photos", ShareKind.Disk, false))
            .Folder(@"\\nas.test\media", "Films", "Music")
            .Folder(@"\\nas.test\media\Films", "2026");
    }

    public void Dispose()
    {
        _network.Release(@"\\nas.test\media");
        _temp.Dispose();
    }

    /// <summary>A node lists what is inside it once, however often it is closed and opened again.</summary>
    [Fact]
    public void ANodeListsWhatIsInsideItOnce() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var media = await OpenMappedShareAsync(browser);

        media.IsExpanded = false;
        await media.OpenAsync();

        Assert.Equal(["Films", "Music"], media.Children.Select(child => child.Name));
        Assert.Equal(1, _network.Asked(@"\\nas.test\media"));
    });

    /// <summary>The roots that list in a moment are opened at first, and the Network root, which waits on discovery, is not.</summary>
    [Fact]
    public void TheFirstShowingOpensEveryRootButNetwork() => UiThread.Run(async () =>
    {
        using var browser = Browser();

        await browser.ShownAsync();

        Assert.False(Root(browser, NetworkRoot.Network).IsExpanded);
        Assert.Equal(0, _network.Asked(FakeNetwork.Discovery));
        Assert.True(Root(browser, NetworkRoot.MappedDrives).IsExpanded);
    });

    /// <summary>A node that fails says so on its own line, offers a sign-in where it refused this account, and leaves the others as they were.</summary>
    [Fact]
    public void AFailureShowsOnThatNodeOnly() => UiThread.Run(async () =>
    {
        _network.FolderFails(@"\\nas.test\media\Music", NetworkFailure.NeedsSignIn);
        using var browser = Browser();
        var media = await OpenMappedShareAsync(browser);
        var films = media.Children.Single(child => child.Name == "Films");
        var music = media.Children.Single(child => child.Name == "Music");

        await films.OpenAsync();
        await music.OpenAsync();

        Assert.Equal(NetworkText.Failure(NetworkFailure.NeedsSignIn), music.FailureText);
        Assert.True(music.CanSignIn);
        Assert.False(films.HasFailure);
        Assert.False(media.HasFailure);
        Assert.Equal(["2026"], films.Children.Select(child => child.Name));
    });

    /// <summary>A node closed while it lists stops listing, shows nothing late, and asks again once opened.</summary>
    [Fact]
    public void ClosingANodeWhileItListsChangesNothing() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var mapped = Root(browser, NetworkRoot.MappedDrives);
        await mapped.OpenAsync();
        var media = mapped.Children.Single();
        _network.Hold(@"\\nas.test\media");

        media.IsExpanded = true;
        Assert.True(media.IsLoading);

        media.IsExpanded = false;
        var listing = media.Listing;
        _network.Release(@"\\nas.test\media");
        await listing;

        Assert.False(media.IsLoading);
        Assert.Empty(media.Children);
        Assert.True(media.HasUnrealizedChildren);

        await media.OpenAsync();

        Assert.Equal(["Films", "Music"], media.Children.Select(child => child.Name));
    });

    /// <summary>A listing that lands as the node is closed, queued behind the closing on the UI thread, is dropped there.</summary>
    [Fact]
    public void AListingQueuedAsTheNodeClosesIsDropped() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var mapped = Root(browser, NetworkRoot.MappedDrives);
        await mapped.OpenAsync();
        var media = mapped.Children.Single();
        _network.Hold(@"\\nas.test\media");

        media.IsExpanded = true;
        var listing = media.Listing;
        _network.Release(@"\\nas.test\media");

        // Held, so the landed listing's continuation is queued behind the closing.
        Thread.Sleep(300);
        media.IsExpanded = false;
        await listing;

        Assert.Empty(media.Children);
        Assert.True(media.HasUnrealizedChildren);
    });

    /// <summary>A computer added by name is kept in the preferences, listed under its root with its shares, and can be taken out again.</summary>
    [Fact]
    public void AnAddedComputerIsStoredListedAndRemoved() => UiThread.Run(async () =>
    {
        using var browser = Browser();

        browser.ComputerText = @"\\nas.test";
        await browser.AddComputerCommand.ExecuteAsync(null);

        Assert.Equal(["nas.test"], _preferences.Current.Locations.Computers);
        var computer = Assert.Single(Root(browser, NetworkRoot.Added).Children);
        Assert.Same(computer, browser.Selected);
        Assert.True(computer.IsRemovable);
        Assert.Equal(["media", "Photos"], computer.Children.Select(child => child.Name));
        Assert.Empty(browser.ComputerText);

        browser.RemoveComputerCommand.Execute(computer);
        await Root(browser, NetworkRoot.Added).Listing;

        Assert.Empty(_preferences.Current.Locations.Computers);
        Assert.Empty(Root(browser, NetworkRoot.Added).Children);
    });

    [Fact]
    public void TextThatNamesNoComputerIsRefusedAndNothingIsStored() => UiThread.Run(async () =>
    {
        using var browser = Browser();

        browser.ComputerText = "my nas";
        await browser.AddComputerCommand.ExecuteAsync(null);

        Assert.Equal(NetworkText.NotAComputer, browser.ComputerRefusal);
        Assert.Empty(_preferences.Current.Locations.Computers);
    });

    /// <summary>The browser opens again with the place it was left at opened and selected.</summary>
    [Fact]
    public void TheBrowserOpensWhereItWasLeft() => UiThread.Run(async () =>
    {
        using (var first = Browser())
        {
            var media = await OpenMappedShareAsync(first);
            var films = media.Children.Single(child => child.Name == "Films");
            first.Selected = films;
            first.Closing();
        }

        using var second = Browser();
        await second.ShownAsync();

        Assert.Equal(@"\\nas.test\media\Films", second.SelectedPath);
        Assert.True(Root(second, NetworkRoot.MappedDrives).Children.Single().IsExpanded);
    });

    /// <summary>A place left at that has gone selects the deepest node still on the way to it.</summary>
    [Fact]
    public void APlaceThatHasGoneSelectsTheWayToIt() => UiThread.Run(async () =>
    {
        _preferences.Update(current => current with
        {
            Locations = current.Locations.LeftAt(NetworkRoot.MappedDrives, @"\\nas.test\media\Gone\Deeper"),
        });

        using var browser = Browser();
        await browser.ShownAsync();

        Assert.Equal(@"\\nas.test\media", browser.SelectedPath);
    });

    /// <summary>A share or folder is a choice to fill the box with. A computer and a heading are not, since neither is a folder.</summary>
    [Fact]
    public void OnlyAFolderIsAChoice() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var media = await OpenMappedShareAsync(browser);

        browser.Selected = media;
        Assert.Equal(@"\\nas.test\media", browser.Choice);

        browser.Selected = Root(browser, NetworkRoot.MappedDrives);
        Assert.Null(browser.Choice);
        Assert.Empty(browser.SelectedPath);

        _preferences.Update(current => current with { Locations = current.Locations.WithComputer("nas.test") });
        var added = Root(browser, NetworkRoot.Added);
        await added.OpenAsync();
        browser.Selected = added.Children.Single();

        Assert.False(browser.CanUse);
        Assert.Equal(@"\\nas.test", browser.SelectedPath);
    });

    /// <summary>
    /// The place chosen fills the Path… box and still goes through its check: a share that is not
    /// there is refused as typed text is, and the page stays where it was.
    /// </summary>
    [Fact]
    public void AChoiceStillGoesThroughTheBoxsCheck() => UiThread.Run(async () =>
    {
        using var explore = new ExploreFixture();
        explore.Volumes.With(@"C:\");
        explore.Probe.Disconnected(@"\\?\UNC\nas.test\media");
        var page = explore.Page();
        using var browser = Browser();
        browser.Selected = await OpenMappedShareAsync(browser);

        page.Targeting.Typed.Text = browser.Choice!;
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(TypedFolder.Refusal(@"\\nas.test\media", PathPresence.Absent), page.Targeting.Typed.Refusal);
        Assert.Equal(@"C:\", page.Targeting.Target.Root);
    });

    /// <summary>The filter narrows the nodes listed by name and keeps the way to a match open; emptied, it shows them all again.</summary>
    [Fact]
    public void TheFilterNarrowsListedNodesAndKeepsTheWayToThem() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var media = await OpenMappedShareAsync(browser);
        await media.Children.Single(child => child.Name == "Films").OpenAsync();

        browser.Filter = "2026";

        Assert.Equal(["Films"], media.Children.Select(child => child.Name));
        Assert.Equal(["2026"], media.Children.Single().Children.Select(child => child.Name));

        browser.Filter = string.Empty;

        Assert.Equal(["Films", "Music"], media.Children.Select(child => child.Name));
    });

    /// <summary>Refreshing a node asks again for it and for what is open inside it, and keeps the rows that are still there.</summary>
    [Fact]
    public void RefreshingANodeAsksAgainAndKeepsItsRows() => UiThread.Run(async () =>
    {
        using var browser = Browser();
        var media = await OpenMappedShareAsync(browser);
        var films = media.Children.Single(child => child.Name == "Films");
        await films.OpenAsync();
        _network.Folder(@"\\nas.test\media", "Films", "Music", "Shows");

        browser.RefreshCommand.Execute(media);
        await media.Listing;
        await films.Listing;

        Assert.Equal(["Films", "Music", "Shows"], media.Children.Select(child => child.Name));
        Assert.Same(films, media.Children[0]);
        Assert.Equal(2, _network.Asked(@"\\nas.test\media"));
        Assert.Equal(2, _network.Asked(@"\\nas.test\media\Films"));
    });

    /// <summary>A sign-in through the credentials prompt lists the node again once connected, owned by the browser's window.</summary>
    [Fact]
    public void SigningInListsTheNodeAgain() => UiThread.Run(async () =>
    {
        _network.Refusing("nas.test", NetworkFailure.NeedsSignIn);
        _preferences.Update(current => current with { Locations = current.Locations.WithComputer("nas.test") });
        using var browser = Browser();
        var added = Root(browser, NetworkRoot.Added);
        await added.OpenAsync();
        var computer = added.Children.Single();
        await computer.OpenAsync();

        Assert.True(computer.CanSignIn);

        _network.Computer("nas.test", new ListedShare("media", ShareKind.Disk, false));
        await browser.SignInCommand.ExecuteAsync(computer);
        await computer.Listing;

        Assert.Equal([(@"\\nas.test\IPC$", (nint)99)], _network.SignIns);
        Assert.False(computer.HasFailure);
        Assert.Equal(["media"], computer.Children.Select(child => child.Name));
    });

    [Fact]
    public void ASignInThatFailsSaysSoOnTheNode() => UiThread.Run(async () =>
    {
        _network.Refusing("nas.test", NetworkFailure.NeedsSignIn);
        _network.SignInAnswer = NetworkSignIn.Failed(NetworkFailure.NoAnswer);
        _preferences.Update(current => current with { Locations = current.Locations.WithComputer("nas.test") });
        using var browser = Browser();
        var added = Root(browser, NetworkRoot.Added);
        await added.OpenAsync();
        var computer = added.Children.Single();
        await computer.OpenAsync();

        await browser.SignInCommand.ExecuteAsync(computer);

        Assert.Equal(NetworkFailure.NoAnswer, computer.Failure);
    });

    private NetworkBrowser Browser() => new(_network.Access, _time, _preferences, owner: 99);

    private static NetworkNodeRow Root(NetworkBrowser browser, NetworkRoot root) => browser.Roots.Single(row => row.Root == root);

    private static async Task<NetworkNodeRow> OpenMappedShareAsync(NetworkBrowser browser)
    {
        var mapped = Root(browser, NetworkRoot.MappedDrives);
        await mapped.OpenAsync();
        var media = mapped.Children.Single();
        await media.OpenAsync();

        return media;
    }
}
