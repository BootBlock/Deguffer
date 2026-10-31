using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the line under Explore's map says of a shape: its path, its space on disk and age, what the
/// colouring paints it for and only under that colouring, and what the reference and Windows say of
/// it, joined.
/// </summary>
public sealed class ExploreReadoutTests
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly VolumeSpace Space = new(1000 * Megabyte, 400 * Megabyte);

    private static readonly ItemGuide NoGuide = new([], new Dictionary<KnownPlace, string>(), new FakeVolumeInventory());

    [Fact]
    public void AShapeSaysItsPathItsSpaceOnDiskAndItsAge()
    {
        var tree = Drive(Written("old.log", 3_000, Now.AddDays(-3)));
        var node = Child(tree, "old.log");

        var readout = ExploreReadout.Over(tree, node, State(ExploreColouring.Branch));

        Assert.Equal(@"C:\old.log", readout.Name);
        Assert.Equal($"{FreeSpace.Format(3_000)} on disk, last written {RelativeAge.Describe(Now.AddDays(-3), Now)}", readout.Figures);
        Assert.Equal(string.Empty, readout.Note);
    }

    [Fact]
    public void WhatTheSpaceOnDiskLeavesOutIsSaidBetweenTheSizeAndTheAge()
    {
        var tree = Drive(new ExploreChild("cloud.bin", IsDirectory: false, IsLink: false, Size: 0, Length: 5_000_000, Storage: FileStorage.CloudOnly));

        var figures = ExploreReadout.Over(tree, Child(tree, "cloud.bin"), State(ExploreColouring.Branch)).Figures;

        Assert.StartsWith($"{FreeSpace.Format(0)} on disk, {FreeSpace.Format(5_000_000)} long, online-only, last written ", figures);
    }

    /// <summary>The kind is said under the type colouring alone, so the colour is never the only thing saying it (§6.5).</summary>
    [Fact]
    public void TheKindIsSaidOnlyUnderTheTypeColouring()
    {
        var tree = Drive(File("film.mp4", 4_000));
        var node = Child(tree, "film.mp4");

        Assert.EndsWith(", video", ExploreReadout.Over(tree, node, State(ExploreColouring.Type)).Figures);
        Assert.DoesNotContain("video", ExploreReadout.Over(tree, node, State(ExploreColouring.Branch)).Figures);
    }

    [Fact]
    public void AFolderSaysItsLargestKindOnceMeasuredAndNothingBefore()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var films = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Films")]);
        builder.AddChildren(films, [File("a.mp4", 4_000)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.DoesNotContain("kind", ExploreReadout.Over(tree, films, State(ExploreColouring.Type)).Figures);
        Assert.EndsWith(
            ", largest kind: video",
            ExploreReadout.Over(tree, films, State(ExploreColouring.Type) with { Dominant = DominantTypes.Measure(tree) }).Figures);
    }

    [Fact]
    public void TheLevelIsSaidOnlyUnderTheDepthColouring()
    {
        var tree = Drive(File("top.txt", 10));
        var node = Child(tree, "top.txt");

        Assert.EndsWith(", 1 level in", ExploreReadout.Over(tree, node, State(ExploreColouring.Depth)).Figures);
        Assert.DoesNotContain("level", ExploreReadout.Over(tree, node, State(ExploreColouring.Type)).Figures);
    }

    [Fact]
    public void AShapesOwnChangeIsSaidOnlyUnderTheGrowthColouring()
    {
        var before = Folders(("Logs", 10 * Megabyte));
        var after = Folders(("Logs", 250 * Megabyte));
        var growth = ScanGrowth.Between(Summary(before, Then), Summary(after, Now), after);
        var logs = Child(after, "Logs");
        var state = State(ExploreColouring.Growth) with { Growth = growth };

        Assert.EndsWith(
            $", {GrowthText.Change(growth.ChangeOf(logs)!.Value, growth.Earlier.UnrecordedAtMost)} since the last scan",
            ExploreReadout.Over(after, logs, state).Figures);
        Assert.DoesNotContain("since the last scan", ExploreReadout.Over(after, logs, state with { Colouring = ExploreColouring.Age }).Figures);
    }

    /// <summary>A file painted by its folder's change has none of its own, and borrows none.</summary>
    [Fact]
    public void AShapeWithNoChangeOfItsOwnSaysNone()
    {
        var before = Folders(("Logs", 10 * Megabyte));
        var after = Folders(("Logs", 250 * Megabyte));
        var growth = ScanGrowth.Between(Summary(before, Then), Summary(after, Now), after);
        var file = after.ChildrenOf(Child(after, "Logs"))[0];

        Assert.Null(growth.ChangeOf(file));
        Assert.DoesNotContain(
            "since the last scan",
            ExploreReadout.Over(after, file, State(ExploreColouring.Growth) with { Growth = growth }).Figures);
    }

    /// <summary>What the reference says first, then what Windows states, apart by an empty line.</summary>
    [Fact]
    public void TheReferenceAndWhatWindowsStatesAreJoined()
    {
        var tree = Drive(Folder(VolumeSpace.SystemVolumeInformation), File("file", 3_000));
        var node = Child(tree, VolumeSpace.SystemVolumeInformation);
        var volume = new VolumeSpace(12_000, 6_000, new HiddenSpace(new ShadowStorage(Statement.Stated, 400, 1_000, 2_000), default), CountedSystemVolumeInformation: true);
        var guide = new ItemGuide(
            [new KnownItem(KnownPlace.VolumeRoot, VolumeSpace.SystemVolumeInformation, "Restore points live here.", "It cannot be deleted.")],
            new Dictionary<KnownPlace, string> { [KnownPlace.VolumeRoot] = @"C:\" },
            new FakeVolumeInventory());
        var stated = HiddenSpaceNote.For(tree, node, volume);
        var described = guide.DescribeNearest(tree.PathOf(node))?.Tip();

        Assert.NotNull(described);
        Assert.NotEqual(string.Empty, stated);
        Assert.Equal($"{described}\n\n{stated}", ExploreReadout.Over(tree, node, State(ExploreColouring.Branch) with { Guide = guide, Volume = volume }).Note);
        Assert.Equal(stated, ExploreReadout.Over(tree, node, State(ExploreColouring.Branch) with { Volume = volume }).Note);
        Assert.Equal(described, ExploreReadout.Over(tree, node, State(ExploreColouring.Branch) with { Guide = guide }).Note);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheUnaccountedBlockSaysWhyForThisProcessAndThisScan(bool isElevated)
    {
        var tree = Drive(File("file", 3_000));
        var state = State(ExploreColouring.Branch) with { Volume = Space, IsElevated = isElevated, Strategy = ScanStrategy.MasterFileTable };

        Assert.Equal(
            ExploreUnaccountedNote.For(isElevated, Space, tree.TotalBytes, ScanStrategy.MasterFileTable),
            ExploreReadout.NoteOn(tree, MapBlock.Unaccounted, state));
    }

    [Fact]
    public void TheHiddenSpaceBlocksSayWhatWindowsStatesAndTheRestSayNothing()
    {
        var tree = Drive(File("file", 3_000));
        var shadow = new ShadowStorage(Statement.Stated, 400, 1_000, 2_000);
        var state = State(ExploreColouring.Branch) with { Volume = Space with { Hidden = new HiddenSpace(shadow, default) } };

        Assert.Equal(HiddenSpaceNote.ShadowCopies(shadow), ExploreReadout.NoteOn(tree, MapBlock.ShadowCopies, state));
        Assert.Equal(HiddenSpaceNote.ReservedStorage(), ExploreReadout.NoteOn(tree, MapBlock.ReservedStorage, state));
        Assert.Equal(string.Empty, ExploreReadout.NoteOn(tree, MapBlock.Aggregate, state));
        Assert.Equal(string.Empty, ExploreReadout.NoteOn(tree, MapBlock.FreeSpace, state));
    }

    private static ExploreReadoutState State(ExploreColouring colouring) =>
        new(Now, colouring, Growth: null, Dominant: null, Current: ExploreTreeBuilder.RootNode, NoGuide, VolumeSpace.None, IsElevated: false, ScanStrategy.ParallelEnumeration);

    private static ExploreTree Drive(params ExploreChild[] children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, children);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A volume of top-level folders, each holding one file of the size given.</summary>
    private static ExploreTree Folders(params (string Name, long Bytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [.. folders.Select(folder => Folder(folder.Name))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [File("data.bin", folders[i].Bytes)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static ScanSummary Summary(ExploreTree tree, DateTime taken) =>
        ScanSummaries.Take(ExploreScan.Fast(tree), Volume, Space, taken);

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child named {name}.");
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, Size: size);

    private static ExploreChild Written(string name, long size, DateTime when) =>
        new(name, IsDirectory: false, IsLink: false, size, ExploreTimestamp.FromUtc(when), ExploreTimestamp.FromUtc(when));
}
