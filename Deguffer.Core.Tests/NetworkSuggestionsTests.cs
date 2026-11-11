using Deguffer.Core.Exploring.Network;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Path… box suggests at each stage of what has been typed, in what order, and what is
/// left out, from sources' answers the test describes.
/// </summary>
public sealed class NetworkSuggestionsTests
{
    private static readonly NetworkSources Sources = new(
        NetworkListing<NetworkConnection>.Listed([
            new NetworkConnection(@"\\nas.test\media", "Z:"),
            new NetworkConnection(@"\\files.test\team", null),
        ]),
        NetworkListing<NetworkConnection>.Listed([
            new NetworkConnection(@"\\nas.test\media", "Z:"),
            new NetworkConnection(@"\\old.test\archive", "Y:"),
            new NetworkConnection(@"\\backup.test\vault", null),
        ]),
        NetworkListing<string>.Listed(["printer.test", "nas.test"]),
        [@"\\files.test\team\Reports", @"D:\Projects"],
        ["vault.test"]);

    /// <summary>
    /// The empty box: mapped drives, connected shares, remembered connections, recent folders, the
    /// Network folder's computers and the added ones, in that order, each place once. A remembered
    /// connection that is connected now is listed as connected, not twice.
    /// </summary>
    [Fact]
    public void TheEmptyBoxListsWhatThisAccountUsesGroupedInOrder()
    {
        var suggestions = NetworkSuggestions.Everything(Sources);

        Assert.Equal(
            [
                (@"\\nas.test\media", NetworkGroup.MappedDrive, "Z:"),
                (@"\\files.test\team", NetworkGroup.Connected, null),
                (@"\\old.test\archive", NetworkGroup.Remembered, "Y:"),
                (@"\\backup.test\vault", NetworkGroup.Remembered, null),
                (@"\\files.test\team\Reports", NetworkGroup.Recent, null),
                (@"D:\Projects", NetworkGroup.Recent, null),
                (@"\\printer.test", NetworkGroup.Network, null),
                (@"\\nas.test", NetworkGroup.Network, null),
                (@"\\vault.test", NetworkGroup.Added, null),
            ],
            suggestions.Select(s => (s.Place.Path, s.Group, s.Place.DriveLetter)));
    }

    /// <summary>A mapped drive says which letter it stands behind, beside where it came from.</summary>
    [Fact]
    public void AMappedDriveShowsItsLetterBesideTheShare()
    {
        var mapped = NetworkSuggestions.Everything(Sources)[0];

        Assert.Equal(NetworkPlaceKind.MappedDrive, mapped.Place.Kind);
        Assert.Equal("Mapped drive, Z:", NetworkText.Detail(mapped));
        Assert.Equal(@"\\nas.test\media\", mapped.Completion);
    }

    /// <summary>
    /// <c>\\</c> typed: every computer this account reaches, from discovery, the added ones, and the
    /// ones its connections and recent folders are on, narrowed by prefix first, then by contained text.
    /// </summary>
    [Fact]
    public void ComputersAreNarrowedByPrefixFirstThenByContainedText()
    {
        var suggestions = NetworkSuggestions.Computers(Sources, "VA");

        Assert.Equal([@"\\vault.test"], suggestions.Select(s => s.Place.Path));

        var holding = NetworkSuggestions.Computers(Sources, ".test");

        Assert.Equal(
            [@"\\printer.test", @"\\nas.test", @"\\vault.test", @"\\files.test", @"\\old.test", @"\\backup.test"],
            holding.Select(s => s.Place.Path));
        Assert.Equal(NetworkGroup.Added, NetworkSuggestions.Computers(Sources, "vault").Single().Group);
        Assert.Equal(NetworkGroup.Network, NetworkSuggestions.Computers(Sources, "nas").Single().Group);
    }

    [Fact]
    public void AComputerNamePrefixComesBeforeAComputerThatOnlyHoldsTheText()
    {
        var sources = NetworkSources.Remembering([], ["backup.test", "atlas.test"]);

        Assert.Equal(
            [@"\\atlas.test", @"\\backup.test"],
            NetworkSuggestions.Computers(sources, "a").Select(s => s.Place.Path));
    }

    /// <summary>
    /// <c>\\server\</c> typed: the shared folders, A to Z. Administrative shares, hidden ones, the
    /// interprocess share, printers and devices are left out.
    /// </summary>
    [Fact]
    public void AComputersSharesLeaveOutAdministrativeHiddenAndPrinterShares()
    {
        var listed = NetworkListing<ListedShare>.Listed([
            new ListedShare("Photos", ShareKind.Disk, IsSpecial: false),
            new ListedShare("C$", ShareKind.Disk, IsSpecial: true),
            new ListedShare("ADMIN$", ShareKind.Disk, IsSpecial: true),
            new ListedShare("IPC$", ShareKind.Ipc, IsSpecial: true),
            new ListedShare("Laser", ShareKind.PrintQueue, IsSpecial: false),
            new ListedShare("Modem", ShareKind.Device, IsSpecial: false),
            new ListedShare("secret$", ShareKind.Disk, IsSpecial: false),

            // Marked administrative without the trailing $, so the mark is what leaves it out.
            new ListedShare("Cluster", ShareKind.Disk, IsSpecial: true),
            new ListedShare("media", ShareKind.Disk, IsSpecial: false),
        ]);

        var suggestions = NetworkSuggestions.Shares("nas.test", listed, string.Empty);

        Assert.Equal([@"\\nas.test\media", @"\\nas.test\Photos"], suggestions.Select(s => s.Place.Path));
        Assert.All(suggestions, s => Assert.Equal(NetworkPlaceKind.Share, s.Place.Kind));
    }

    [Fact]
    public void AComputersSharesAreNarrowedByWhatFollowsTheSeparator()
    {
        var listed = NetworkListing<ListedShare>.Listed([
            new ListedShare("media", ShareKind.Disk, IsSpecial: false),
            new ListedShare("Photos", ShareKind.Disk, IsSpecial: false),
            new ListedShare("Home Media", ShareKind.Disk, IsSpecial: false),
        ]);

        Assert.Equal(
            [@"\\nas.test\media", @"\\nas.test\Home Media"],
            NetworkSuggestions.Shares("nas.test", listed, "MED").Select(s => s.Place.Path));
    }

    /// <summary>A share that refused lists nothing to suggest, rather than suggesting what an earlier stage listed.</summary>
    [Fact]
    public void ASourceThatFailedSuggestsNothing()
    {
        Assert.Empty(NetworkSuggestions.Shares("nas.test", NetworkListing<ListedShare>.Failed(NetworkFailure.NeedsSignIn), string.Empty));
        Assert.Empty(NetworkSuggestions.Folders(@"\\nas.test\media", NetworkListing<string>.Failed(NetworkFailure.NotFound), string.Empty));
    }

    [Fact]
    public void AFoldersFoldersAreListedOneLevelInAToZ()
    {
        var suggestions = NetworkSuggestions.Folders(
            @"\\nas.test\media\", NetworkListing<string>.Listed(["Photos", "music", "Films"]), string.Empty);

        Assert.Equal(
            [@"\\nas.test\media\Films", @"\\nas.test\media\music", @"\\nas.test\media\Photos"],
            suggestions.Select(s => s.Place.Path));
        Assert.All(suggestions, s => Assert.Equal(NetworkPlaceKind.Folder, s.Place.Kind));
        Assert.All(suggestions, s => Assert.Equal(string.Empty, NetworkText.Detail(s)));
    }
}
