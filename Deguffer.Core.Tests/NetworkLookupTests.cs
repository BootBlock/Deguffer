using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Network;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a lookup session answers the Path… box and the network browser from the sources: what has
/// answered is shown at once, and a slow or failing source neither holds up nor empties the others.
/// </summary>
public sealed class NetworkLookupTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly ExploreLocations Remembered = ExploreLocations.None with
    {
        Recent = [@"\\files.test\team\Reports"],
        Computers = ["vault.test"],
    };

    private readonly FakeNetwork _network = new();
    private readonly ManualTimeProvider _time = new();
    private readonly NetworkLookup _lookup;

    public NetworkLookupTests()
    {
        _network.ConnectedAnswer = NetworkListing<NetworkConnection>.Listed([new NetworkConnection(@"\\nas.test\media", "Z:")]);
        _network.ComputersAnswer = NetworkListing<string>.Listed(["printer.test"]);
        _lookup = new NetworkLookup(_network.Access, _time);
    }

    public void Dispose()
    {
        _lookup.Dispose();
        _network.Release(FakeNetwork.Discovery);
    }

    /// <summary>
    /// The empty box shows what Explore remembers at once, the connections as they answer, and the
    /// Network folder's computers only once discovery does: discovery waiting holds nothing else up.
    /// </summary>
    [Fact]
    public async Task ASlowSourceDoesNotHoldUpTheOthers()
    {
        _network.Hold(FakeNetwork.Discovery);

        await using var lists = _lookup.SuggestAsync(string.Empty, Remembered, CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await lists.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal([@"\\files.test\team\Reports", @"\\vault.test"], Paths(lists.Current));

        // Connected and remembered connections answer in either order, and each is a list of its own.
        IReadOnlyList<NetworkSuggestion> withConnections;

        do
        {
            Assert.True(await lists.MoveNextAsync().AsTask().WaitAsync(Patience));
            withConnections = lists.Current;
        }
        while (!withConnections.Any(s => s.Group is NetworkGroup.MappedDrive));

        Assert.DoesNotContain(withConnections, s => s.Group is NetworkGroup.Network);

        _network.Release(FakeNetwork.Discovery);

        IReadOnlyList<NetworkSuggestion> all;

        do
        {
            Assert.True(await lists.MoveNextAsync().AsTask().WaitAsync(Patience));
            all = lists.Current;
        }
        while (!all.Any(s => s.Group is NetworkGroup.Network));

        Assert.Equal(
            [@"\\nas.test\media", @"\\files.test\team\Reports", @"\\printer.test", @"\\vault.test"],
            Paths(all));
    }

    /// <summary>A failing source is left out, and empties none of the others.</summary>
    [Fact]
    public async Task AFailingSourceEmptiesNoneOfTheOthers()
    {
        _network.ComputersAnswer = NetworkListing<string>.Failed(NetworkFailure.Refused);

        var last = await LastAsync(_lookup.SuggestAsync(string.Empty, Remembered, CancellationToken.None));

        Assert.Equal([@"\\nas.test\media", @"\\files.test\team\Reports", @"\\vault.test"], Paths(last));
    }

    /// <summary>
    /// A share stage whose computer has not answered yet first empties the list, so the last stage's
    /// computers are never offered under the next stage's text.
    /// </summary>
    [Fact]
    public async Task AStageStillAskingEmptiesTheListFirst()
    {
        _network.Computer("nas.test", new ListedShare("media", ShareKind.Disk, false));
        _network.Hold("nas.test");

        await using var lists = _lookup.SuggestAsync(@"\\nas.test\", Remembered, CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await lists.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Empty(lists.Current);

        _network.Release("nas.test");

        Assert.True(await lists.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.Equal([@"\\nas.test\media"], Paths(lists.Current));
        Assert.False(await lists.MoveNextAsync());
    }

    /// <summary>A stage answered from what the session kept is one list, with no empty one before it.</summary>
    [Fact]
    public async Task AStageAnsweredFromWhatWasKeptIsOneList()
    {
        _network.Folder(@"\\nas.test\media", "Photos", "Films");

        await LastAsync(_lookup.SuggestAsync(@"\\nas.test\media\", Remembered, CancellationToken.None));

        var lists = new List<IReadOnlyList<NetworkSuggestion>>();

        await foreach (var list in _lookup.SuggestAsync(@"\\nas.test\media\Ph", Remembered, CancellationToken.None))
        {
            lists.Add(list);
        }

        Assert.Equal([@"\\nas.test\media\Photos"], Paths(Assert.Single(lists)));
        Assert.Equal(1, _network.Asked(@"\\nas.test\media"));
    }

    /// <summary>A computer's sign-in goes through its <c>IPC$</c> share, and a folder's through the share it is in.</summary>
    [Fact]
    public async Task ASignInIsAskedOfTheShareOrTheComputer()
    {
        await _lookup.SignInAsync(NetworkSources.Computer("nas.test"), 7, CancellationToken.None);
        await _lookup.SignInAsync(new NetworkPlace(@"\\nas.test\media\Photos", "Photos", NetworkPlaceKind.Folder), 7, CancellationToken.None);

        Assert.Equal([@"\\nas.test\IPC$", @"\\nas.test\media"], _network.SignIns.Select(signIn => signIn.Remote));
    }

    [Fact]
    public async Task OpeningAPlaceListsWhatItHolds()
    {
        _network.Computer("nas.test", new ListedShare("media", ShareKind.Disk, false), new ListedShare("C$", ShareKind.Disk, true));
        _network.Folder(@"\\nas.test\media", "Photos");

        var shares = await _lookup.ChildrenAsync(NetworkSources.Computer("nas.test"), CancellationToken.None);
        var folders = await _lookup.ChildrenAsync(shares.Entries.Single(), CancellationToken.None);

        Assert.Equal([@"\\nas.test\media"], shares.Entries.Select(place => place.Path));
        Assert.Equal([@"\\nas.test\media\Photos"], folders.Entries.Select(place => place.Path));
    }

    [Fact]
    public async Task ARootListsFromTheSourcesItReads()
    {
        var mapped = await _lookup.RootAsync(NetworkRoot.MappedDrives, Remembered, CancellationToken.None);
        var added = await _lookup.RootAsync(NetworkRoot.Added, Remembered, CancellationToken.None);

        Assert.Equal([@"\\nas.test\media"], mapped.Entries.Select(place => place.Path));
        Assert.Equal([@"\\vault.test"], added.Entries.Select(place => place.Path));
        Assert.Equal(0, _network.Asked(FakeNetwork.Discovery));
    }

    private static string[] Paths(IReadOnlyList<NetworkSuggestion> suggestions) => [.. suggestions.Select(s => s.Place.Path)];

    private static async Task<IReadOnlyList<NetworkSuggestion>> LastAsync(IAsyncEnumerable<IReadOnlyList<NetworkSuggestion>> lists)
    {
        IReadOnlyList<NetworkSuggestion> last = [];

        await foreach (var list in lists.WithCancellation(new CancellationTokenSource(Patience).Token))
        {
            last = list;
        }

        return last;
    }
}
