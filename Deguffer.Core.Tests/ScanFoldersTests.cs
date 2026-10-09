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
