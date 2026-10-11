using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The map replaying what grew between two scans (#285), from synthesised trees: the replay reads only
/// the earlier summary, the comparison and the tree now, so the trees are the whole input.
/// </summary>
public class GrowthReplayTests
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private const int Steps = GrowthReplay.Steps;

    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly VolumeSpace Space = new(1000 * Megabyte, 400 * Megabyte);

    [Fact]
    public void EachSizeMovesFromWhatTheEarlierScanRecordedToWhatIsThereNow()
    {
        var after = Tree(("Logs", 250 * Megabyte), ("Same", 20 * Megabyte));
        var replay = Replay(Tree(("Logs", 10 * Megabyte), ("Same", 20 * Megabyte)), after);
        var logs = Child(after, "Logs");

        Assert.Equal(10 * Megabyte, replay.At(0).SizeOf(logs));
        Assert.Equal(130 * Megabyte, replay.At(Steps / 2).SizeOf(logs));
        Assert.Equal(250 * Megabyte, replay.At(Steps).SizeOf(logs));
        Assert.Equal(30 * Megabyte, replay.At(0).SizeOf(after.RootNode));
        Assert.Equal(after.TotalBytes, replay.At(Steps).SizeOf(after.RootNode));
        Assert.True(replay.Recorded(logs));
        Assert.Same(replay.At(3), replay.At(3));
    }

    /// <summary>A folder the earlier scan recorded and the disk no longer holds starts at its earlier size and shrinks away.</summary>
    [Fact]
    public void AFolderRemovedSinceStartsAtItsEarlierSizeAndShrinksAway()
    {
        var after = Tree(("Logs", 10 * Megabyte));
        var replay = Replay(Tree(("Logs", 10 * Megabyte), ("Old", 30 * Megabyte)), after);
        var earliest = replay.At(0);

        var children = earliest.ChildrenOf(after.RootNode).ToArray();
        var old = children[0];

        Assert.True(old >= replay.TodayCount);
        Assert.Equal([old, Child(after, "Logs")], children);
        Assert.Equal(30 * Megabyte, earliest.SizeOf(old));
        Assert.Equal(0, replay.At(Steps).SizeOf(old));
        Assert.Equal("Old, removed since  30.0 MB", earliest.Label(old));
        Assert.Equal(after.RootNode, earliest.ParentOf(old));
        Assert.Equal(-30 * Megabyte, replay.ChangeOf(old));

        // The tree now holds nothing of it, so the step after the last is the tree now.
        Assert.Null(earliest.CounterpartsIn(after)!(old));
    }

    /// <summary>
    /// Below a recorded folder, what the earlier scan did not record one by one is one block that says
    /// so, and what is there now grows from nothing beside it. At every step the folder's children add
    /// up to the folder.
    /// </summary>
    [Fact]
    public void WhatTheEarlierScanDidNotRecordIsOneBlockPerFolderThatSaysSo()
    {
        var after = Tree(("Logs", 250 * Megabyte));
        var replay = Replay(Tree(("Logs", 10 * Megabyte)), after);
        var logs = Child(after, "Logs");
        var file = OnlyChild(after, logs);
        var earliest = replay.At(0);

        var block = earliest.ChildrenOf(logs)[0];

        Assert.Equal([block, file], earliest.ChildrenOf(logs).ToArray());
        Assert.False(earliest.IsContainer(block));
        Assert.Equal(10 * Megabyte, earliest.SizeOf(block));
        Assert.Equal(0, earliest.SizeOf(file));
        Assert.Equal("Files, not recorded  10.0 MB", earliest.Label(block));
        Assert.Null(replay.ChangeOf(block));

        Assert.Equal([file, block], replay.At(Steps).ChildrenOf(logs).ToArray());
        Assert.Equal(0, replay.At(Steps).SizeOf(block));

        for (var step = 0; step <= Steps; step++)
        {
            var frame = replay.At(step);
            Assert.Equal(frame.SizeOf(logs), frame.SizeOf(block) + frame.SizeOf(file), Megabyte / 1000);
        }
    }

    /// <summary>A summary that stopped short of a folder names the floor it stopped at on the block.</summary>
    [Fact]
    public void ABlockNamesHowSmallTheFoldersInItWere()
    {
        var before = Tree(("Big", 100 * Megabyte), ("Middle", 50 * Megabyte), ("Small", 5 * Megabyte));
        var summary = ScanSummaries.Take(ExploreScan.Fast(before), Volume, Space, Then, limit: 3);
        var growth = ScanGrowth.Between(summary, Summary(before, Now), before);
        var replay = GrowthReplay.Of(growth, before)!;
        var earliest = replay.At(0);

        var block = Assert.Single(earliest.ChildrenOf(before.RootNode).ToArray(), node => node >= replay.TodayCount);

        Assert.Equal(5 * Megabyte, summary.UnrecordedAtMost);
        Assert.Equal(5 * Megabyte, earliest.SizeOf(block));
        Assert.Equal("Files and folders under 5.0 MB, not recorded  5.0 MB", earliest.Label(block));
        Assert.False(replay.Recorded(Child(before, "Small")));
        Assert.Equal(0, earliest.SizeOf(Child(before, "Small")));
    }

    /// <summary>
    /// Siblings are laid out largest first at each step, so one that grows past another moves ahead of
    /// it, and two of one size keep their name order (#309) rather than trading places.
    /// </summary>
    [Fact]
    public void SiblingsAreOrderedBySizeAtEachStepAndByNameWhenEqual()
    {
        var after = Tree(("Alpha", 10 * Megabyte), ("Beta", 30 * Megabyte));
        var replay = Replay(Tree(("Alpha", 30 * Megabyte), ("Beta", 10 * Megabyte)), after);
        var alpha = Child(after, "Alpha");
        var beta = Child(after, "Beta");

        Assert.Equal([alpha, beta], replay.At(0).ChildrenOf(after.RootNode).ToArray());
        Assert.Equal([alpha, beta], replay.At(Steps / 2).ChildrenOf(after.RootNode).ToArray());
        Assert.Equal([beta, alpha], replay.At(Steps).ChildrenOf(after.RootNode).ToArray());
        Assert.Equal(ExploreChildOrder.BySize, replay.At(0).ChildOrder);
    }

    /// <summary>
    /// A node is the same node at every step and in the tree now, which is what lets the map move from
    /// one step to the next, and into and out of the replay.
    /// </summary>
    [Fact]
    public void ANodeIsTheSameNodeAtEveryStepAndInTheTreeNow()
    {
        var after = Tree(("Logs", 10 * Megabyte));
        var replay = Replay(Tree(("Logs", 10 * Megabyte), ("Old", 30 * Megabyte)), after);
        var logs = Child(after, "Logs");
        var old = replay.TodayCount;

        Assert.Equal(old, replay.At(0).CounterpartsIn(replay.At(1))!(old));
        Assert.Equal(logs, replay.At(0).CounterpartsIn(replay.At(Steps))!(logs));
        Assert.Equal(logs, replay.At(0).CounterpartsIn(after)!(logs));
        Assert.Equal(logs, after.CounterpartsIn(replay.At(0))!(logs));

        // Another tree is another place, even of the same drive.
        var other = Tree(("Logs", 10 * Megabyte));
        Assert.Null(replay.At(0).CounterpartsIn(other));
        Assert.Null(other.CounterpartsIn(replay.At(0)));
    }

    /// <summary>
    /// §7.1: a replay is a picture only. A drawing of a step answers nothing, whichever tree the page
    /// holds, and nothing the page names is found in it, so nothing in it can be picked or removed.
    /// </summary>
    [Fact]
    public void NothingInAReplayAnswersThePointer()
    {
        var after = Tree(("Logs", 250 * Megabyte));
        var frame = Replay(Tree(("Logs", 10 * Megabyte)), after).At(0);

        Assert.False(MapReading.Answers(frame));
        Assert.Null(MapReading.Of(frame, frame));
        Assert.Null(MapReading.Of(frame, after));
        Assert.Null(MapReading.Of(after, frame));

        // The tree now still answers for itself.
        Assert.True(MapReading.Answers(after));
        Assert.NotNull(MapReading.Of(after, after));
    }

    /// <summary>
    /// §7.1 again, where the pointer meets the picture: a point on a drawing of a step finds the shape
    /// drawn there, and the map is answered nothing, whether the page holds the step or the tree now.
    /// </summary>
    [Fact]
    public void APointOnADrawingOfAStepIsOverNothing()
    {
        var after = Tree(("Logs", 250 * Megabyte));
        var frame = Replay(Tree(("Logs", 10 * Megabyte)), after).At(0);
        var drawing = ExploreSurface.Create(
            frame, frame.RootNode, ExploreView.Treemap, 400, 300, 1, 1,
            ShapeColours.ForReplay(frame), ExploreSpacing.Comfortable, VolumeSpace.None);

        Assert.NotNull(drawing.At(200, 150));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.5, 0.5, drawing, null, tree => MapReading.Of(tree, frame)));
        Assert.Null(MapHitTest.Locate(MapViewport.Whole, 0.5, 0.5, drawing, null, tree => MapReading.Of(tree, after)));
    }

    /// <summary>
    /// A play moves one step at a time where the shapes travel, and from the earlier scan straight to
    /// now where the reader has animation effects off (#273), resting long enough on each step for it
    /// to be seen.
    /// </summary>
    [Fact]
    public void APlayMovesOneStepAtATimeOrStraightToNowWithAnimationEffectsOff()
    {
        var travels = Viewing.MotionToken.Shift.For(true);
        var still = Viewing.MotionToken.Shift.For(false);

        Assert.Equal(1, GrowthReplay.Next(0, travels));
        Assert.Equal(Steps, GrowthReplay.Next(Steps, travels));
        Assert.Equal(Steps, GrowthReplay.Next(0, still));
        Assert.True(GrowthReplay.Pace(travels) > travels.Duration);
        Assert.True(GrowthReplay.Pace(still) >= TimeSpan.FromSeconds(1));
    }

    /// <summary>A folder taken out from the page since the scan is gone from the disk, so the replay shrinks it away.</summary>
    [Fact]
    public void AFolderTakenOutFromThePageIsReplayedAsRemoved()
    {
        var after = Tree(("Logs", 10 * Megabyte), ("Same", 20 * Megabyte));
        var growth = Compare(Tree(("Logs", 10 * Megabyte), ("Same", 20 * Megabyte)), after);
        var logs = Child(after, "Logs");
        var left = after.Without([logs]);

        var replay = GrowthReplay.Of(growth, left)!;
        var earliest = replay.At(0);
        var gone = Assert.Single(earliest.ChildrenOf(left.RootNode).ToArray(), node => node >= replay.TodayCount);

        Assert.False(replay.Recorded(logs));
        Assert.Equal("Logs, removed since  10.0 MB", earliest.Label(gone));
        Assert.DoesNotContain(logs, earliest.ChildrenOf(left.RootNode).ToArray());
        Assert.Equal(left.TotalBytes, replay.At(Steps).SizeOf(left.RootNode));
    }

    [Fact]
    public void AReplayIsOnlyOfTheTreeItsComparisonDescribes()
    {
        var after = Tree(("Logs", 10 * Megabyte));
        var growth = Compare(Tree(("Logs", 10 * Megabyte)), after);

        Assert.Null(GrowthReplay.Of(growth, Tree(("Logs", 10 * Megabyte))));
        Assert.Null(GrowthReplay.Of(null, after));
        Assert.NotNull(GrowthReplay.Of(growth, after));
    }

    /// <summary>
    /// The volume beside a step moves from the earlier scan's figures to now's. What Windows states
    /// about the space no folder accounts for was read with this scan only, so nothing earlier states it.
    /// </summary>
    [Fact]
    public void TheVolumeMovesFromTheEarlierFiguresToNowsAndStatesNoHiddenSpaceBeforeNow()
    {
        var before = Tree(("Logs", 10 * Megabyte));
        var after = Tree(("Logs", 250 * Megabyte));
        var growth = ScanGrowth.Between(
            ScanSummaries.Take(ExploreScan.Fast(before), Volume, new VolumeSpace(1000 * Megabyte, 400 * Megabyte), Then),
            Summary(after, Now),
            after);
        var replay = GrowthReplay.Of(growth, after)!;
        var now = new VolumeSpace(
            1200 * Megabyte,
            300 * Megabyte,
            new Exploring.Hidden.HiddenSpace(default, new Exploring.Hidden.ReservedStorage(Exploring.Hidden.Statement.Stated, 7 * Megabyte)),
            CountedSystemVolumeInformation: true);

        Assert.Equal(new VolumeSpace(1000 * Megabyte, 400 * Megabyte, CountedSystemVolumeInformation: true), replay.VolumeAt(0, now));
        Assert.Equal(new VolumeSpace(1100 * Megabyte, 350 * Megabyte, CountedSystemVolumeInformation: true), replay.VolumeAt(Steps / 2, now));
        Assert.Equal(now, replay.VolumeAt(Steps, now));
        Assert.Equal(VolumeSpace.None, replay.VolumeAt(0, VolumeSpace.None));
    }

    /// <summary>
    /// Every shape keeps the colour of the change it ends with at every step, as the map coloured by
    /// growth paints the tree now, and the colours of one step describe that step and nothing else.
    /// </summary>
    [Fact]
    public void EachShapeKeepsTheColourOfTheChangeItEndsWith()
    {
        var after = Tree(("Logs", 250 * Megabyte));
        var growth = Compare(Tree(("Logs", 10 * Megabyte)), after);
        var replay = GrowthReplay.Of(growth, after)!;
        var logs = Child(after, "Logs");

        Assert.Equal(growth.BytesAt(logs), replay.ChangeOf(logs));
        Assert.Equal(240 * Megabyte, replay.ChangeOf(logs));

        ShapeColours.ForReplay(replay.At(2)).EnsureDescribes(replay.At(2));
        Assert.Throws<ArgumentException>(() => ShapeColours.ForReplay(replay.At(2)).EnsureDescribes(replay.At(3)));
        Assert.Throws<ArgumentException>(() => ShapeColours.ForReplay(replay.At(2)).EnsureDescribes(after));
    }

    /// <summary>
    /// A step is laid out as any treemap is: the folder removed since is drawn at the earlier end and
    /// not at the later, and a folder that is new is drawn at the later end and not at the earlier.
    /// </summary>
    [Fact]
    public void AStepIsLaidOutAsATreemap()
    {
        var after = Tree(("Logs", 10 * Megabyte), ("New", 30 * Megabyte));
        var replay = Replay(Tree(("Logs", 10 * Megabyte), ("Old", 30 * Megabyte)), after);
        var old = replay.TodayCount;
        var created = Child(after, "New");

        var earliest = Drawn(replay.At(0));
        var latest = Drawn(replay.At(Steps));

        Assert.Contains(old, earliest);
        Assert.DoesNotContain(created, earliest);
        Assert.Contains(created, latest);
        Assert.DoesNotContain(old, latest);

        static HashSet<int> Drawn(GrowthReplayFrame frame) =>
            [.. TreemapLayout.Compute(frame, frame.RootNode, 400, 300, LayoutLimits.Default).Select(tile => tile.Node)];
    }

    private static GrowthReplay Replay(ExploreTree before, ExploreTree after) =>
        GrowthReplay.Of(Compare(before, after), after)!;

    private static ScanGrowth Compare(ExploreTree before, ExploreTree after) =>
        ScanGrowth.Between(Summary(before, Then), Summary(after, Now), after);

    private static ScanSummary Summary(ExploreTree tree, DateTime taken) =>
        ScanSummaries.Take(ExploreScan.Fast(tree), Volume, Space, taken);

    /// <summary>A volume of top-level folders, each holding one file of the size given.</summary>
    private static ExploreTree Tree(params (string Name, long Bytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [new ExploreChild("data.bin", IsDirectory: false, IsLink: false, Size: folders[i].Bytes)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int OnlyChild(ExploreTree tree, int folder) => Assert.Single(tree.ChildrenOf(folder).ToArray());

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
