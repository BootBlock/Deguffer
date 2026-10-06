using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What grew between two scans of one volume (#260), compared from synthesised trees rather than a
/// disk: the comparison reads only the summary and the tree, so the trees are the whole input.
/// </summary>
public class ScanGrowthTests
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly VolumeSpace Space = new(1000 * Megabyte, 400 * Megabyte);

    /// <summary>The three cases the issue names: a folder that grew by N, one that was removed, and one that did not change.</summary>
    [Fact]
    public void AFolderThatGrewIsListedWithItsGrowthAndARemovedOneAsRemoved()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte), ("Old", 30 * Megabyte), ("Same", 20 * Megabyte));
        var after = Tree(@"C:\", ("Logs", 250 * Megabyte), ("Same", 20 * Megabyte));

        var growth = Compare(before, after);

        var logs = growth.ChangeOf(Child(after, "Logs"))!.Value;
        Assert.Equal(FolderChangeKind.Measured, logs.Kind);
        Assert.Equal(240 * Megabyte, logs.Bytes);
        Assert.Equal("+240 MB", GrowthText.Change(logs, growth.Earlier.UnrecordedAtMost));

        var same = growth.ChangeOf(Child(after, "Same"))!.Value;
        Assert.Equal(0, same.Bytes);
        Assert.Equal("No change", GrowthPalette.BandOf(same.Bytes).Label);

        var listing = growth.Listing();
        Assert.Equal(@"C:\Logs", listing[0].Path);
        Assert.DoesNotContain(listing, change => change.Path == @"C:\Same");

        var old = Assert.Single(listing, change => change.Kind == FolderChangeKind.Removed);
        Assert.Equal(@"C:\Old", old.Path);
        Assert.Equal(-30 * Megabyte, old.Bytes);
        Assert.Equal(-1, old.Node);
        Assert.Equal(old.Path, listing[^1].Path);
    }

    [Fact]
    public void TheListRanksGrowthLargestFirstAndShrinkageLargestLast()
    {
        var before = Tree(@"C:\", ("A", 100 * Megabyte), ("B", 100 * Megabyte), ("C", 100 * Megabyte), ("D", 100 * Megabyte));
        var after = Tree(@"C:\", ("A", 110 * Megabyte), ("B", 400 * Megabyte), ("C", 90 * Megabyte), ("D", 20 * Megabyte));

        var paths = Compare(before, after).Listing().Select(change => change.Path).ToList();

        Assert.Equal([@"C:\B", @"C:\", @"C:\A", @"C:\C", @"C:\D"], paths);
    }

    /// <summary>
    /// A folder too small to be recorded then and large now is new, or was no larger then than the
    /// earlier summary's floor, and it says so rather than claiming its whole size as growth.
    /// </summary>
    [Fact]
    public void AFolderUnrecordedThenIsNewOrUnderTheFloor()
    {
        var before = Tree(@"C:\", ("Big", 500 * Megabyte), ("Tiny", 1 * Megabyte), ("Small", 2 * Megabyte));
        var after = Tree(@"C:\", ("Big", 500 * Megabyte), ("Tiny", 300 * Megabyte), ("Small", 2 * Megabyte));

        // Room for the root and Big only, so Small, at 2 MB, is the largest folder left out.
        var earlier = ScanSummaries.Take(ExploreScan.Fast(before), Volume, Space, Then, limit: 2);
        Assert.Equal(2 * Megabyte, earlier.UnrecordedAtMost);

        var growth = ScanGrowth.Between(earlier, Summary(after, Now), after);

        var tiny = growth.ChangeOf(Child(after, "Tiny"))!.Value;
        Assert.Equal(FolderChangeKind.New, tiny.Kind);
        Assert.Equal(300 * Megabyte, tiny.Bytes);
        Assert.Equal("+300 MB, new or under 2.0 MB then", GrowthText.Change(tiny, earlier.UnrecordedAtMost));

        // No larger than the floor either time, so nothing can be said about it.
        Assert.Null(growth.ChangeOf(Child(after, "Small")));
    }

    /// <summary>
    /// A shape with no record of its own is painted by the nearest folder above it that has one, and
    /// a shape of another tree is painted as not compared.
    /// </summary>
    [Fact]
    public void AShapeIsPaintedByTheNearestComparedFolderAndNothingInAnotherTree()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte));
        var after = Tree(@"C:\", ("Logs", 2000 * Megabyte));
        var growth = Compare(before, after);

        var logs = Child(after, "Logs");
        var file = after.ChildrenOf(logs)[0];

        Assert.Null(growth.ChangeOf(file));
        Assert.Equal("Grew 1 GB or more", GrowthPalette.BandOf(growth.BytesAt(file)).Label);

        var grew = GrowthPalette.BandOf(growth.BytesAt(file)).Colour;
        var colours = ShapeColours.For(after, ExploreColouring.Growth, ExploreScheme.Standard, Now, growth);
        var surface = ExploreSurface.Create(
            after, after.RootNode, ExploreView.Icicle, 200, 200, 1, 1, colours, ExploreSpacing.Comfortable, VolumeSpace.None);
        Assert.Equal(grew, colours.For(surface, file, depth: 2));

        // A comparison of one tree describes nothing in another, even one with the same shape.
        var another = Tree(@"C:\", ("Logs", 2000 * Megabyte));
        var stale = ShapeColours.For(another, ExploreColouring.Growth, ExploreScheme.Standard, Now, growth);
        var unrelated = ExploreSurface.Create(
            another, another.RootNode, ExploreView.Icicle, 200, 200, 1, 1, stale, ExploreSpacing.Comfortable, VolumeSpace.None);
        Assert.Equal(GrowthPalette.BandOf((long?)null).Colour, stale.For(unrelated, file, depth: 2));
    }

    /// <summary>
    /// Each change falls in the band that names it, a boundary belongs to the larger band, and growth
    /// and shrinkage of the same size never share a colour.
    /// </summary>
    [Theory]
    [InlineData(2048, "Grew 1 GB or more")]
    [InlineData(1024, "Grew 1 GB or more")]
    [InlineData(1023, "Grew 100 MB to 1 GB")]
    [InlineData(100, "Grew 100 MB to 1 GB")]
    [InlineData(1, "Grew under 100 MB")]
    [InlineData(0, "No change")]
    [InlineData(-1, "Shrank under 100 MB")]
    [InlineData(-100, "Shrank 100 MB to 1 GB")]
    [InlineData(-1024, "Shrank 1 GB or more")]
    public void EachChangeFallsInTheBandThatNamesIt(long megabytes, string expected)
    {
        var change = new FolderChange(@"C:\Logs", FolderChangeKind.Measured, 4096 * Megabyte, (4096 + megabytes) * Megabyte, 1);

        Assert.Equal(expected, GrowthPalette.BandOf(change.Bytes).Label);

        var mirrored = new FolderChange(@"C:\Logs", FolderChangeKind.Measured, 4096 * Megabyte, (4096 - megabytes) * Megabyte, 1);
        Assert.Equal(megabytes == 0, GrowthPalette.For(change.Bytes) == GrowthPalette.For(mirrored.Bytes));
    }

    [Fact]
    public void NothingComparedHasItsOwnBand() =>
        Assert.Equal("Not compared", GrowthPalette.BandOf((long?)null).Label);

    /// <summary>
    /// The growth scale darkens away from the middle on each side, so its order reads in grey as well
    /// as in colour.
    /// </summary>
    [Fact]
    public void EachSideOfTheScaleDarkensAwayFromTheMiddle()
    {
        var luminance = GrowthPalette.Bands.Take(7).Select(band => band.Colour.RelativeLuminance).ToList();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(luminance[i] < luminance[i + 1], $"{GrowthPalette.Bands[i].Label} is not darker than the band after it.");
            Assert.True(luminance[6 - i] < luminance[5 - i], $"{GrowthPalette.Bands[6 - i].Label} is not darker than the band before it.");
        }

        Assert.True(GrowthPalette.Bands[^1].Colour.RelativeLuminance < luminance[3], "Not compared is not darker than no change.");
    }

    /// <summary>
    /// A file's own dates settle its colour before the folder above it is asked: one made since the
    /// earlier scan is growth by its whole size, one not written since is unchanged, and only one
    /// written since with nothing to say how much takes the folder's colour. Without this, every old
    /// file directly under a folder that grew is painted as having grown with it.
    /// </summary>
    [Fact]
    public void AFilesOwnDatesSettleItsColourBeforeItsFolders()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte));

        var longAgo = ExploreTimestamp.FromUtc(Then.AddYears(-3));
        var since = ExploreTimestamp.FromUtc(Then.AddDays(3));

        var builder = new ExploreTreeBuilder(@"C:\");
        var logs = builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("Logs", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("old.mp4", IsDirectory: false, IsLink: false, Size: 900 * Megabyte, longAgo, longAgo),
            new ExploreChild("new.bin", IsDirectory: false, IsLink: false, Size: 300 * Megabyte, since, since),
        ]);
        builder.AddChildren(logs, [new ExploreChild("app.log", IsDirectory: false, IsLink: false, Size: 2000 * Megabyte, longAgo, since)]);
        var after = builder.Build(ExploreChildOrder.BySize);

        var growth = Compare(before, after);
        var old = Child(after, "old.mp4");
        var made = Child(after, "new.bin");
        var log = after.ChildrenOf(Child(after, "Logs"))[0];

        // The root grew, which old.mp4 must not borrow.
        Assert.True(growth.ChangeOf(after.RootNode)!.Value.Bytes > 0);

        Assert.Equal(0, growth.BytesAt(old));
        Assert.Equal("No change", GrowthPalette.BandOf(growth.BytesAt(old)).Label);
        Assert.Equal(FolderChangeKind.Measured, growth.ChangeOf(old)!.Value.Kind);

        Assert.Equal(300 * Megabyte, growth.BytesAt(made));
        var created = growth.ChangeOf(made)!.Value;
        Assert.Equal(FolderChangeKind.Created, created.Kind);
        Assert.Equal("+300 MB, created since then", GrowthText.Change(created, growth.Earlier.UnrecordedAtMost));

        // Written since, made long before: how much is not known, so it takes its folder's change.
        Assert.Null(growth.ChangeOf(log));
        Assert.Equal(growth.ChangeOf(Child(after, "Logs"))!.Value.Bytes, growth.BytesAt(log));
    }

    /// <summary>
    /// A date is a whole minute, rounded down, so a file written in the minute the earlier scan
    /// finished may have been written after it, and is not called unchanged.
    /// </summary>
    [Fact]
    public void AWriteInTheMinuteTheEarlierScanFinishedIsNotCalledUnchanged()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte));
        var earlier = ScanSummaries.Take(ExploreScan.Fast(before), Volume, Space, Then.AddSeconds(30));

        var created = ExploreTimestamp.FromUtc(Then.AddYears(-1));
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("Logs", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("same-minute.txt", IsDirectory: false, IsLink: false, Size: Megabyte, created, ExploreTimestamp.FromUtc(Then.AddSeconds(50))),
            new ExploreChild("minute-before.txt", IsDirectory: false, IsLink: false, Size: Megabyte, created, ExploreTimestamp.FromUtc(Then.AddSeconds(-10))),
        ]);
        var after = builder.Build(ExploreChildOrder.BySize);

        var growth = ScanGrowth.Between(earlier, Summary(after, Now), after);

        Assert.Null(growth.ChangeOf(Child(after, "same-minute.txt")));
        Assert.Equal(0, growth.ChangeOf(Child(after, "minute-before.txt"))!.Value.Bytes);
    }

    /// <summary>A walk counts less than the file table, so a comparison across the two says it is approximate.</summary>
    [Fact]
    public void AComparisonBetweenAWalkAndAFileTableScanIsApproximate()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte));
        var after = Tree(@"C:\", ("Logs", 20 * Megabyte));

        var earlier = ScanSummaries.Take(ExploreScan.Walked(before, FallbackReason.NotElevated), Volume, Space, Then);
        var growth = ScanGrowth.Between(earlier, Summary(after, Now), after);

        Assert.Equal(GrowthApproximation.RouteChanged, growth.Approximation);
        Assert.StartsWith("Approximate:", GrowthText.Approximate(growth.Approximation));
        Assert.Equal(GrowthApproximation.None, Compare(before, after).Approximation);
        Assert.Null(GrowthText.Approximate(GrowthApproximation.None));
    }

    [Fact]
    public void AComparisonWithALowerBoundIsApproximate()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte));
        var after = Tree(@"C:\", ("Logs", 20 * Megabyte), unknownUnder: "Logs");

        Assert.Equal(GrowthApproximation.LowerBound, Compare(before, after).Approximation);
    }

    /// <summary>
    /// A change of route and a lower bound are wrong in different ways, so where both hold, both are
    /// said: §7.1 asks a lower bound to say so whatever else is true.
    /// </summary>
    [Fact]
    public void ARouteChangeAndALowerBoundAreBothStated()
    {
        var before = Tree(@"C:\", ("Logs", 10 * Megabyte), unknownUnder: "Logs");
        var after = Tree(@"C:\", ("Logs", 20 * Megabyte));

        var earlier = ScanSummaries.Take(ExploreScan.Walked(before, FallbackReason.NotElevated), Volume, Space, Then);
        var growth = ScanGrowth.Between(earlier, Summary(after, Now), after);

        Assert.Equal(GrowthApproximation.RouteChanged | GrowthApproximation.LowerBound, growth.Approximation);

        var said = GrowthText.Approximate(growth.Approximation)!;
        Assert.Contains("different ways", said, StringComparison.Ordinal);
        Assert.Contains("lower bounds", said, StringComparison.Ordinal);
    }

    /// <summary>A disk scanned as E: and again as F: is compared with itself, by its folders rather than its letter.</summary>
    [Fact]
    public void AVolumeThatChangedLetterStillCompares()
    {
        var before = Tree(@"E:\", ("Logs", 10 * Megabyte));
        var after = Tree(@"F:\", ("Logs", 20 * Megabyte), ("New", 0));

        var growth = Compare(before, after);

        Assert.Equal(10 * Megabyte, growth.ChangeOf(Child(after, "Logs"))!.Value.Bytes);
        Assert.Equal(@"F:\Logs", growth.ChangeOf(Child(after, "Logs"))!.Value.Path);
        Assert.DoesNotContain(growth.Ranked, change => change.Kind == FolderChangeKind.Removed);
    }

    /// <summary>
    /// A summary keeps the largest folders and every folder above each of them, records no files and
    /// no links, and states the most any folder it left out held.
    /// </summary>
    [Fact]
    public void ASummaryKeepsTheLargestFoldersWithTheirParents()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new("Users", IsDirectory: true, IsLink: false, Size: 0),
            new("Small", IsDirectory: true, IsLink: false, Size: 0),
            new("Link", IsDirectory: true, IsLink: true, Size: 0),
            new("pagefile.sys", IsDirectory: false, IsLink: false, Size: 900 * Megabyte),
        ]);
        var deep = builder.AddChildren(first, [new("me", IsDirectory: true, IsLink: false, Size: 0)]);
        builder.AddChildren(deep, [new("data.bin", IsDirectory: false, IsLink: false, Size: 50 * Megabyte)]);
        builder.AddChildren(first + 1, [new("a.txt", IsDirectory: false, IsLink: false, Size: 5 * Megabyte)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var summary = ScanSummaries.Take(ExploreScan.Fast(tree), Volume, Space, Now, limit: 3);

        Assert.Equal([@"C:\", "Users", "me"], summary.Folders.Select(folder => folder.Name));
        Assert.Equal([-1, 0, 1], summary.Folders.Select(folder => folder.Parent));
        Assert.Equal(5 * Megabyte, summary.UnrecordedAtMost);
        Assert.Equal(@"D:\Users\me", summary.PathOf(2, @"D:\"));
        Assert.Equal(600 * Megabyte, summary.UsedBytes);

        var whole = ScanSummaries.Take(ExploreScan.Fast(tree), Volume, Space, Now);
        Assert.Equal(0, whole.UnrecordedAtMost);
        Assert.DoesNotContain(whole.Folders, folder => folder.Name is "Link" or "pagefile.sys");
    }

    private static ScanGrowth Compare(ExploreTree before, ExploreTree after) =>
        ScanGrowth.Between(Summary(before, Then), Summary(after, Now), after);

    private static ScanSummary Summary(ExploreTree tree, DateTime taken) =>
        ScanSummaries.Take(ExploreScan.Fast(tree), Volume, Space, taken);

    /// <summary>A volume of top-level folders, each holding one file of the size given.</summary>
    private static ExploreTree Tree(string root, params (string Name, long Bytes)[] folders) =>
        Tree(root, unknownUnder: null, folders);

    private static ExploreTree Tree(string root, (string Name, long Bytes) a, (string Name, long Bytes) b, string unknownUnder) =>
        Tree(root, unknownUnder, [a, b]);

    private static ExploreTree Tree(string root, (string Name, long Bytes) a, string unknownUnder) =>
        Tree(root, unknownUnder, [a]);

    private static ExploreTree Tree(string root, string? unknownUnder, (string Name, long Bytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(root);
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [new ExploreChild("data.bin", IsDirectory: false, IsLink: false, Size: folders[i].Bytes)]);

            if (folders[i].Name == unknownUnder)
            {
                builder.MarkSizeUnknown(first + i);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child called {name}.");
    }
}
