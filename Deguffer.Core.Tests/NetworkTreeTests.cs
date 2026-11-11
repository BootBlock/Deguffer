using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Tests;

/// <summary>What the network browser's tree holds: its roots, what each lists, and the way back to a place.</summary>
public sealed class NetworkTreeTests
{
    private static readonly NetworkSources Sources = new(
        NetworkListing<NetworkConnection>.Listed([
            new NetworkConnection(@"\\nas.test\media", "Z:"),
            new NetworkConnection(@"\\files.test\team", null),
        ]),
        NetworkListing<NetworkConnection>.Listed([
            new NetworkConnection(@"\\nas.test\media", "Z:"),
            new NetworkConnection(@"\\backup.test\vault", null),
        ]),
        NetworkListing<string>.Listed(["nas.test", "NAS.test"]),
        [@"\\files.test\team\Reports"],
        ["vault.test"]);

    [Fact]
    public void TheRootsGoFromWhatThisAccountUsesToWhatItCanFind() =>
        Assert.Equal(
            [NetworkRoot.MappedDrives, NetworkRoot.ConnectedShares, NetworkRoot.Recent, NetworkRoot.Network, NetworkRoot.Added],
            NetworkTree.Roots);

    [Fact]
    public void EachRootListsItsOwnPlacesEachOnce()
    {
        Assert.Equal([@"\\nas.test\media"], Paths(NetworkRoot.MappedDrives));
        Assert.Equal([@"\\files.test\team", @"\\backup.test\vault"], Paths(NetworkRoot.ConnectedShares));
        Assert.Equal([@"\\files.test\team\Reports"], Paths(NetworkRoot.Recent));
        Assert.Equal([@"\\nas.test"], Paths(NetworkRoot.Network));
        Assert.Equal([@"\\vault.test"], Paths(NetworkRoot.Added));
    }

    /// <summary>A Network folder that refused fails its own root, and leaves the others listed.</summary>
    [Fact]
    public void AFailedSourceFailsOnlyTheRootThatReadsIt()
    {
        var sources = Sources with { Computers = NetworkListing<string>.Failed(NetworkFailure.Refused) };

        Assert.Equal(NetworkFailure.Refused, NetworkTree.Listed(NetworkRoot.Network, sources).Failure);
        Assert.Null(NetworkTree.Listed(NetworkRoot.MappedDrives, sources).Failure);
    }

    /// <summary>The connection roots fail only where both connection sources failed and nothing was listed.</summary>
    [Fact]
    public void AConnectionRootFailsOnlyWhereBothConnectionSourcesFailed()
    {
        var oneFailed = Sources with { Remembered = NetworkListing<NetworkConnection>.Failed(NetworkFailure.Refused) };
        var bothFailed = oneFailed with { Connected = NetworkListing<NetworkConnection>.Failed(NetworkFailure.Refused) };

        Assert.Null(NetworkTree.Listed(NetworkRoot.ConnectedShares, oneFailed).Failure);
        Assert.Equal(NetworkFailure.Refused, NetworkTree.Listed(NetworkRoot.ConnectedShares, bothFailed).Failure);
    }

    [Fact]
    public void AComputerListsSharesAndEverythingElseListsFolders()
    {
        Assert.Equal(NetworkChildren.Shares, NetworkTree.ChildrenOf(NetworkSources.Computer("nas.test")));

        foreach (var kind in new[] { NetworkPlaceKind.Share, NetworkPlaceKind.MappedDrive, NetworkPlaceKind.Folder, NetworkPlaceKind.Recent })
        {
            Assert.Equal(NetworkChildren.Folders, NetworkTree.ChildrenOf(new NetworkPlace(@"\\nas.test\media", "media", kind)));
        }
    }

    /// <summary>A computer whose share listing failed says why, rather than listing no shares.</summary>
    [Fact]
    public void AComputerThatRefusedSaysSoRatherThanListingNothing()
    {
        var listed = NetworkTree.Shares(NetworkSources.Computer("nas.test"), NetworkListing<ListedShare>.Failed(NetworkFailure.NeedsSignIn));

        Assert.Equal(NetworkFailure.NeedsSignIn, listed.Failure);
        Assert.Empty(listed.Entries);
    }

    /// <summary>The way back to a place goes through the deepest child on the way, so a folder named like its sibling is not taken for it.</summary>
    [Fact]
    public void TheWayBackToAPlaceGoesThroughTheChildOnTheWay()
    {
        var children = new[]
        {
            new NetworkPlace(@"\\nas.test\media", "media", NetworkPlaceKind.Share),
            new NetworkPlace(@"\\nas.test\mediaroom", "mediaroom", NetworkPlaceKind.Share),
        };

        Assert.Equal(children[1], NetworkTree.Toward(children, @"\\nas.test\mediaroom\Films"));
        Assert.Equal(children[0], NetworkTree.Toward(children, @"\\NAS.test\media"));
        Assert.Null(NetworkTree.Toward(children, @"\\nas.test\photos"));
    }

    [Fact]
    public void OnlyTheNetworkRootWaitsToBeOpened() =>
        Assert.Equal([NetworkRoot.Network], NetworkTree.Roots.Where(root => !NetworkTree.OpensAtFirst(root)));

    /// <summary>Only a computer added by name can be taken out of the tree: everything else comes back from Windows.</summary>
    [Fact]
    public void OnlyAnAddedComputerCanBeRemoved()
    {
        var computer = NetworkSources.Computer("nas.test");
        var share = new NetworkPlace(@"\\nas.test\media", "media", NetworkPlaceKind.Share);

        Assert.True(NetworkTree.CanRemove(NetworkRoot.Added, computer));
        Assert.False(NetworkTree.CanRemove(NetworkRoot.Network, computer));
        Assert.False(NetworkTree.CanRemove(NetworkRoot.Added, share));
    }

    /// <summary>A place that refused this account offers the credentials prompt. A root, or another failure, does not.</summary>
    [Fact]
    public void OnlyAPlaceThatRefusedThisAccountOffersASignIn()
    {
        var computer = NetworkSources.Computer("nas.test");

        Assert.True(NetworkTree.OffersSignIn(computer, NetworkFailure.NeedsSignIn));
        Assert.False(NetworkTree.OffersSignIn(null, NetworkFailure.NeedsSignIn));
        Assert.False(NetworkTree.OffersSignIn(computer, NetworkFailure.NoAnswer));
        Assert.False(NetworkTree.OffersSignIn(computer, null));
    }

    [Fact]
    public void AComputerIsNoFolderToScan()
    {
        Assert.False(NetworkSources.Computer("nas.test").IsFolder);
        Assert.True(new NetworkPlace(@"\\nas.test\media", "media", NetworkPlaceKind.Share).IsFolder);
    }

    /// <summary>The filter keeps a node whose name holds it, and the way to anything kept further in.</summary>
    [Theory]
    [InlineData("Photos", "pho", false, true)]
    [InlineData("media", "pho", true, true)]
    [InlineData("media", "pho", false, false)]
    [InlineData("media", "", false, true)]
    public void TheFilterKeepsMatchesAndTheWayToThem(string name, string filter, bool keepsInside, bool kept) =>
        Assert.Equal(kept, NetworkMatch.Keeps(name, filter, keepsInside));

    private static IEnumerable<string> Paths(NetworkRoot root) => NetworkTree.Listed(root, Sources).Entries.Select(place => place.Path);
}
