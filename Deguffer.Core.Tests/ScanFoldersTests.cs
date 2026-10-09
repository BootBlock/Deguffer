using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="ExploreScanner.ScanFoldersAsync"/>: several folders, each volume's file table read once
/// however many of them are on it, and the walk wherever the table cannot answer.
/// </summary>
public sealed class ScanFoldersTests
{
    private static MftFixture TwoFolders()
    {
        var first = MftRecord.ReservedRecordCount;

        return new MftFixture()
            .AddDirectory(first, MftRecord.RootRecordNumber, "Photos")
            .AddFile(first + 1, first, "a.jpg", allocated: 4096, logical: 100)
            .AddDirectory(first + 2, MftRecord.RootRecordNumber, "Music")
            .AddFile(first + 3, first + 2, "b.mp3", allocated: 4096, logical: 200);
    }

    /// <summary>Each read of a table is the whole table, so two folders on one volume must cost one.</summary>
    [Fact]
    public async Task TwoFoldersOnOneVolumeReadItsTableOnce()
    {
        var sources = FakeMftSourceFactory.Serving('X', TwoFolders());
        var scanner = new ExploreScanner(sources, tuning: RouteTuners.Table);

        var scans = await scanner.ScanFoldersAsync([@"X:\Photos", @"X:\Music"]);

        Assert.Equal(1, sources.OpenCount);
        Assert.All(scans, scan => Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy));
        Assert.Equal(@"X:\Photos\a.jpg", scans[0].Tree.PathOf(Assert.Single(scans[0].Tree.ChildrenOf(scans[0].Node).ToArray())));
        Assert.Equal(@"X:\Music\b.mp3", scans[1].Tree.PathOf(Assert.Single(scans[1].Tree.ChildrenOf(scans[1].Node).ToArray())));
    }

    /// <summary>A folder the table holds no record for is walked, and says why.</summary>
    [Fact]
    public async Task AFolderTheTableDoesNotHoldIsWalked()
    {
        using var temp = new TempDirectory();
        var sources = FakeMftSourceFactory.Serving(Path.GetFullPath(temp.Path)[0], TwoFolders());
        temp.CreateFile(10, "a.bin");

        var scan = Assert.Single(await new ExploreScanner(sources, tuning: RouteTuners.Table).ScanFoldersAsync([temp.Path]));

        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal(FallbackReason.MasterFileTableIncomplete, scan.Fallback);
        Assert.Equal("a.bin", scan.Tree.NameOf(Assert.Single(scan.Tree.ChildrenOf(scan.Node).ToArray())));
    }

    /// <summary>
    /// A folder is a final path, in the case the disk holds, so a table holding the name only in
    /// another case holds another folder: in a case-sensitive directory, <c>photos</c> beside a
    /// <c>Photos</c> the table did not place. Read from the table, the wrong folder's content would be
    /// searched under the right one's name, so the folder is walked.
    /// </summary>
    [Fact]
    public async Task AFolderTheTableHoldsOnlyInAnotherCaseIsWalked()
    {
        using var temp = new TempDirectory();
        var photos = temp.CreateDirectory("Photos");
        temp.CreateFile(10, "Photos", "a.bin");
        Assert.True(VolumePath.TryParse(photos, out var volume));

        var (fixture, parent, next) = Through(volume.Components.SkipLast(1));
        fixture.AddDirectory(next, parent, "photos").AddFile(next + 1, next, "other.bin", allocated: 4096, logical: 100);
        var sources = FakeMftSourceFactory.Serving(volume.DriveLetter, fixture);

        var scan = Assert.Single(await new ExploreScanner(sources, tuning: RouteTuners.Table).ScanFoldersAsync([photos]));

        Assert.Equal(1, sources.OpenCount);
        Assert.Equal(ScanStrategy.ParallelEnumeration, scan.Strategy);
        Assert.Equal("a.bin", scan.Tree.NameOf(Assert.Single(scan.Tree.ChildrenOf(scan.Node).ToArray())));
    }

    /// <summary>Where the table holds the folder in both cases, the folder asked for is the one read.</summary>
    [Fact]
    public async Task AFolderTheTableHoldsInBothCasesIsReadByItsExactName()
    {
        var (fixture, parent, next) = Through([]);
        fixture
            .AddDirectory(next, parent, "photos")
            .AddFile(next + 1, next, "other.bin", allocated: 4096, logical: 100)
            .AddDirectory(next + 2, parent, "Photos")
            .AddFile(next + 3, next + 2, "a.jpg", allocated: 4096, logical: 100);

        var scan = Assert.Single(await new ExploreScanner(FakeMftSourceFactory.Serving('X', fixture), tuning: RouteTuners.Table)
            .ScanFoldersAsync([@"X:\Photos"]));

        Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy);
        Assert.Equal(@"X:\Photos\a.jpg", scan.Tree.PathOf(Assert.Single(scan.Tree.ChildrenOf(scan.Node).ToArray())));
    }

    /// <summary>A table holding <paramref name="folders"/>, each inside the one before it, below the volume's top.</summary>
    /// <returns>The table, the record of the last folder, and the first record number still free.</returns>
    private static (MftFixture Fixture, uint Parent, uint Next) Through(IEnumerable<string> folders)
    {
        var fixture = new MftFixture();
        uint parent = MftRecord.RootRecordNumber;
        uint next = MftRecord.ReservedRecordCount;

        foreach (var folder in folders)
        {
            fixture.AddDirectory(next, parent, folder);
            parent = next++;
        }

        return (fixture, parent, next);
    }

    [Fact]
    public async Task TheWalkSaysWhichFilesAreHiddenOrSystemFiles()
    {
        using var temp = new TempDirectory();
        File.SetAttributes(temp.CreateFile(10, "hidden.bin"), FileAttributes.Hidden);
        File.SetAttributes(temp.CreateFile(10, "system.bin"), FileAttributes.System);
        temp.CreateFile(10, "plain.bin");

        var scan = Assert.Single(await new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated))
            .ScanFoldersAsync([temp.Path]));

        FileVisibility Of(string name) =>
            scan.Tree.VisibilityOf(scan.Tree.ChildrenOf(scan.Node).ToArray().Single(node => scan.Tree.NameOf(node) == name));

        Assert.Equal(FileVisibility.Hidden, Of("hidden.bin"));
        Assert.Equal(FileVisibility.System, Of("system.bin"));
        Assert.Equal(FileVisibility.Shown, Of("plain.bin"));
    }
}
