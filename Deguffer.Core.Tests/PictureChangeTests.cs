using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the map does when it is handed a picture: the same one again keeps its zoom, a scan's next
/// snapshot or what a removal left is the same place laid out again, a change of view carries the
/// shapes, and a change of folder flies in, out or through, or changes at once.
/// </summary>
public sealed class PictureChangeTests
{
    private static readonly VolumeSpace Volume = new(TotalBytes: 100_000_000, FreeBytes: 40_000_000);

    private static readonly MapViewport Zoomed = MapViewport.Anchored(4, 0.3, 0.6, 0.5, 0.5, MapCeiling.Least);

    /// <summary>
    /// The same picture handed again, as a new colouring or scheme does, is no change: the zoom stays,
    /// and a page going back to where the reader had zoomed on it glides there.
    /// </summary>
    [Fact]
    public void TheSamePictureAgainKeepsItsZoomAndGlidesToOneAskedFor()
    {
        var tree = Nested();
        var picture = Treemap(tree, Find(tree, "top0"));

        var again = PictureChange.Between(picture, picture, viewport: null, zoomable: true);
        var back = PictureChange.Between(picture, picture, Zoomed, zoomable: true);

        Assert.False(again.Another);
        Assert.False(again.SamePlace);
        Assert.False(again.Morph);
        Assert.Null(again.Glide);
        Assert.Equal(Zoomed, back.Glide);
        Assert.False(back.Another);
    }

    /// <summary>A picture that cannot be zoomed has nowhere to glide to, on a map that does not zoom or in a view that does not.</summary>
    [Fact]
    public void APictureThatDoesNotZoomNeitherGlidesNorOpensZoomed()
    {
        var tree = Nested();
        var treemap = Treemap(tree, tree.RootNode);
        var icicle = treemap with { View = ExploreView.Icicle };

        Assert.Null(PictureChange.Between(treemap, treemap, Zoomed, zoomable: false).Glide);
        Assert.Null(PictureChange.Between(icicle, icicle, Zoomed, zoomable: true).Glide);
        Assert.Equal(MapViewport.Whole, PictureChange.Between(icicle, treemap, Zoomed, zoomable: false).Opened);
        Assert.Equal(MapViewport.Whole, PictureChange.Between(treemap, icicle, Zoomed, zoomable: true).Opened);
    }

    /// <summary>
    /// Another folder opens at the zoom a page going back or forward asked for, drawn there from the
    /// start, and at the whole of it otherwise.
    /// </summary>
    [Fact]
    public void AnotherFolderOpensAtTheZoomAskedForOrWhole()
    {
        var tree = Nested();
        var root = Treemap(tree, tree.RootNode);
        var top = Treemap(tree, Find(tree, "top0"));

        var revisited = PictureChange.Between(root, top, Zoomed, zoomable: true);
        var opened = PictureChange.Between(root, top, viewport: null, zoomable: true);

        Assert.True(revisited.Another);
        Assert.Equal(Zoomed, revisited.Opened);
        Assert.Null(revisited.Glide);
        Assert.Equal(MapViewport.Whole, opened.Opened);
    }

    /// <summary>
    /// Another tree is another picture, even of the same place drawn the same way: a picture is of one
    /// tree object, whatever the trees hold.
    /// </summary>
    [Fact]
    public void ATreeLikeTheOneShownIsAnotherPicture()
    {
        var first = Nested();
        var second = Nested();

        Assert.NotEqual(Treemap(first, first.RootNode), Treemap(second, second.RootNode));
        Assert.Equal(Treemap(first, first.RootNode), Treemap(first, first.RootNode));
        Assert.True(PictureChange.Between(Treemap(first, first.RootNode), Treemap(second, second.RootNode), null, zoomable: true).Another);
    }

    /// <summary>
    /// A scan's next snapshot of the folder on screen is the same place laid out again, found through
    /// the counterpart of the folder shown, so the shapes move rather than the picture being replaced.
    /// </summary>
    [Fact]
    public void AScansNextSnapshotIsTheSamePlace()
    {
        var earlier = Nested();
        var later = Nested(extra: 5_000);
        var folder = Find(earlier, "top1");

        var change = PictureChange.Between(Treemap(earlier, folder), Treemap(later, Find(later, "top1")), null, zoomable: true);

        Assert.True(change.Another);
        Assert.True(change.SamePlace);
        Assert.False(change.Morph);
    }

    /// <summary>What a removal left is the same place laid out again, though the volume beside it has more room free.</summary>
    [Fact]
    public void WhatARemovalLeftIsTheSamePlace()
    {
        var tree = Nested();
        var left = tree.Without([Find(tree, "mid00")]);
        var freed = Volume with { FreeBytes = Volume.FreeBytes + 1_000 };

        var change = PictureChange.Between(
            Treemap(tree, tree.RootNode) with { Volume = Volume },
            Treemap(left, left.RootNode) with { Volume = freed },
            null,
            zoomable: true);

        Assert.True(change.SamePlace);
    }

    /// <summary>
    /// A new tree is not the same place laid out again where it is drawn another way, the volume is
    /// beside one and not the other, the node asked for is not the counterpart of the one shown, or a
    /// page going back or forward asked for a zoom of its own.
    /// </summary>
    [Fact]
    public void ANewTreeDrawnAnotherWayOrOfAnotherFolderIsNotTheSamePlace()
    {
        var earlier = Nested();
        var later = Nested(extra: 5_000);
        var shown = Treemap(earlier, earlier.RootNode);
        var asked = Treemap(later, later.RootNode);

        Assert.True(PictureChange.Between(shown, asked, null, zoomable: true).SamePlace);
        Assert.False(PictureChange.Between(shown, asked with { View = ExploreView.Sunburst }, null, zoomable: true).SamePlace);
        Assert.False(PictureChange.Between(shown, asked with { Volume = Volume }, null, zoomable: true).SamePlace);
        Assert.False(PictureChange.Between(shown, asked with { Node = Find(later, "top0") }, null, zoomable: true).SamePlace);
        Assert.False(PictureChange.Between(shown, asked, Zoomed, zoomable: true).SamePlace);
    }

    /// <summary>
    /// The same place laid out again gives the counterparts it was found through, so the map carries
    /// the keyboard and a screen reader's folders into the new tree without reading them again; and
    /// anything else gives none, though a new tree of another folder has counterparts of its own.
    /// </summary>
    [Fact]
    public void OnlyTheSamePlaceGivesTheCounterpartsItWasFoundThrough()
    {
        var earlier = Nested();
        var later = Nested(extra: 5_000);
        var shown = Treemap(earlier, earlier.RootNode);
        var asked = Treemap(later, later.RootNode);

        var counterparts = PictureChange.Between(shown, asked, null, zoomable: true).Counterparts;

        Assert.NotNull(counterparts);
        Assert.Equal(Find(later, "mid00"), counterparts(Find(earlier, "mid00")));
        Assert.Null(PictureChange.Between(shown, asked with { Node = Find(later, "top0") }, null, zoomable: true).Counterparts);
        Assert.Null(PictureChange.Between(shown, asked with { View = ExploreView.Sunburst }, null, zoomable: true).Counterparts);
        Assert.Null(PictureChange.Between(shown, asked, Zoomed, zoomable: true).Counterparts);
        Assert.Null(PictureChange.Between(shown, shown, null, zoomable: true).Counterparts);
    }

    /// <summary>
    /// A finished scan drawn as the treemap picked, after its snapshots were drawn as icicles, is a
    /// change of view rather than the same place laid out again, though the view asked for is the same.
    /// </summary>
    [Fact]
    public void AFinishedScanDrawnAfterItsSnapshotsIsNotTheSamePlace()
    {
        var scanning = Nested(order: ExploreChildOrder.ByName);
        var finished = Nested();

        var change = PictureChange.Between(Treemap(scanning, scanning.RootNode), Treemap(finished, finished.RootNode), null, zoomable: true);

        Assert.True(change.Another);
        Assert.False(change.SamePlace);
    }

    /// <summary>
    /// The same place in another of the views that draw a picture carries its shapes there, but not on
    /// a page going back or forward, nor for another folder or another volume beside it.
    /// </summary>
    [Fact]
    public void AChangeOfViewOfTheSamePlaceMorphs()
    {
        var tree = Nested();
        var treemap = Treemap(tree, tree.RootNode);
        var sunburst = treemap with { View = ExploreView.Sunburst };

        var change = PictureChange.Between(treemap, sunburst, null, zoomable: true);

        Assert.True(change.Another);
        Assert.True(change.Morph);
        Assert.False(change.SamePlace);
        Assert.False(PictureChange.Between(treemap, sunburst, Zoomed, zoomable: true).Morph);
        Assert.False(PictureChange.Between(treemap, sunburst with { Node = Find(tree, "top0") }, null, zoomable: true).Morph);
        Assert.False(PictureChange.Between(treemap with { Volume = Volume }, sunburst, null, zoomable: true).Morph);
        Assert.False(PictureChange.Between(treemap, treemap with { View = ExploreView.List }, null, zoomable: true).Morph);
    }

    /// <summary>A scan still running is drawn as an icicle whichever view is picked, so a change of view during one carries nothing.</summary>
    [Fact]
    public void AChangeOfViewDuringAScanDoesNotMorph()
    {
        var scanning = Nested(order: ExploreChildOrder.ByName);
        var treemap = Treemap(scanning, scanning.RootNode);

        Assert.False(PictureChange.Between(treemap, treemap with { View = ExploreView.Sunburst }, null, zoomable: true).Morph);
    }

    /// <summary>
    /// A folder below is a step in, one above a step out, and one beside a step through the nearest
    /// folder that holds both, which only a step through names.
    /// </summary>
    [Fact]
    public void AChangeOfFolderStepsInOutOrThrough()
    {
        var tree = Nested();
        var top0 = Find(tree, "top0");
        var mid00 = Find(tree, "mid00");
        var mid01 = Find(tree, "mid01");

        Assert.Equal(new PictureStep(FolderStep.Into, null), StepBetween(tree, tree.RootNode, mid00));
        Assert.Equal(new PictureStep(FolderStep.OutOf, null), StepBetween(tree, mid00, tree.RootNode));
        Assert.Equal(new PictureStep(FolderStep.Through, top0), StepBetween(tree, mid00, mid01));
        Assert.Equal(new PictureStep(FolderStep.Through, tree.RootNode), StepBetween(tree, mid00, Find(tree, "mid10")));
    }

    /// <summary>The root opened out of its volume is a step in, and closed back into it a step out.</summary>
    [Fact]
    public void TheRootOpenedOutOfItsVolumeStepsInAndClosedStepsOut()
    {
        var tree = Nested();
        var beside = Treemap(tree, tree.RootNode) with { Volume = Volume };
        var opened = Treemap(tree, tree.RootNode);

        Assert.Equal(FolderStep.Into, PictureChange.Between(beside, opened, null, zoomable: true).Step(drawingZooms: true).Way);
        Assert.Equal(FolderStep.OutOf, PictureChange.Between(opened, beside, null, zoomable: true).Step(drawingZooms: true).Way);
    }

    /// <summary>
    /// A folder with nothing in it to draw has nothing to fly into: a step through to it changes at
    /// once. A step into it still flies, and lands when nothing arrives.
    /// </summary>
    [Fact]
    public void AStepThroughToAnEmptyFolderChangesAtOnce()
    {
        var tree = Nested(empty: true);
        var empty = Find(tree, "empty");

        Assert.Equal(PictureStep.Across, StepBetween(tree, Find(tree, "mid00"), empty));
        Assert.Equal(FolderStep.Into, StepBetween(tree, tree.RootNode, empty).Way);
    }

    /// <summary>
    /// Only a treemap the map can zoom flies, between folders of one tree in one view, from a drawing
    /// on screen placed by the zoom. Anything else changes at once.
    /// </summary>
    [Fact]
    public void OnlyAZoomableTreemapOfOneTreeFlies()
    {
        var tree = Nested();
        var other = Nested();
        var root = Treemap(tree, tree.RootNode);
        var top = Treemap(tree, Find(tree, "top0"));

        Assert.Equal(FolderStep.Into, PictureChange.Between(root, top, null, zoomable: true).Step(drawingZooms: true).Way);
        Assert.Equal(PictureStep.Across, PictureChange.Between(root, top, null, zoomable: true).Step(drawingZooms: false));
        Assert.Equal(PictureStep.Across, PictureChange.Between(root, top, null, zoomable: false).Step(drawingZooms: true));
        Assert.Equal(PictureStep.Across, PictureChange.Between(root with { View = ExploreView.Icicle }, top with { View = ExploreView.Icicle }, null, zoomable: true).Step(drawingZooms: true));
        Assert.Equal(PictureStep.Across, PictureChange.Between(root with { View = ExploreView.Icicle }, top, null, zoomable: true).Step(drawingZooms: true));
        Assert.Equal(PictureStep.Across, PictureChange.Between(root, Treemap(other, Find(other, "top0")), null, zoomable: true).Step(drawingZooms: true));
    }

    /// <summary>A scan still running is drawn as an icicle, which has no camera to fly.</summary>
    [Fact]
    public void AChangeOfFolderDuringAScanDoesNotFly()
    {
        var scanning = Nested(order: ExploreChildOrder.ByName);

        Assert.Equal(PictureStep.Across, StepBetween(scanning, scanning.RootNode, Find(scanning, "top0")));
    }

    /// <summary>
    /// A flight asked for the picture it is leaving, at the zoom it left it at, turns round. At any
    /// other zoom, or for any other picture, it is another flight, and the same picture is no flight.
    /// </summary>
    [Fact]
    public void AFlightAskedForThePictureItLeftAtItsZoomTurnsRound()
    {
        var tree = Nested();
        var left = Treemap(tree, tree.RootNode);
        var going = Treemap(tree, Find(tree, "top0"));

        Assert.True(PictureChange.Between(going, left, Zoomed, zoomable: true).Returns(left, Zoomed));
        Assert.True(PictureChange.Between(going, left, null, zoomable: true).Returns(left, MapViewport.Whole));
        Assert.False(PictureChange.Between(going, left, null, zoomable: true).Returns(left, Zoomed));
        Assert.False(PictureChange.Between(going, left with { View = ExploreView.Sunburst }, null, zoomable: true).Returns(left, MapViewport.Whole));
        Assert.False(PictureChange.Between(left, left, null, zoomable: true).Returns(left, MapViewport.Whole));
    }

    private static PictureStep StepBetween(ExploreTree tree, int from, int to) =>
        PictureChange.Between(Treemap(tree, from), Treemap(tree, to), null, zoomable: true).Step(drawingZooms: true);

    private static MapPicture Treemap(ExploreTree tree, int node) => new(tree, node, ExploreView.Treemap, VolumeSpace.None);

    private static int Find(ExploreTree tree, string name)
    {
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        throw new InvalidOperationException($"no node named {name}");
    }

    /// <summary>
    /// Two folders of two folders of two files under one root, every size different, with
    /// <paramref name="extra"/> bytes more in the last file, as a scan's next snapshot finds, and, where
    /// <paramref name="empty"/>, an empty folder beside the others.
    /// </summary>
    private static ExploreTree Nested(long extra = 0, bool empty = false, ExploreChildOrder order = ExploreChildOrder.BySize)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                .. Enumerable.Range(0, 2).Select(i => new ExploreChild($"top{i}", IsDirectory: true, IsLink: false, Size: 0)),
                .. empty ? [new ExploreChild("empty", IsDirectory: true, IsLink: false, Size: 0)] : Array.Empty<ExploreChild>(),
            ]);
        var file = 0;

        for (var i = 0; i < 2; i++)
        {
            var middle = builder.AddChildren(
                top + i,
                [.. Enumerable.Range(0, 2).Select(j => new ExploreChild($"mid{i}{j}", IsDirectory: true, IsLink: false, Size: 0))]);

            for (var j = 0; j < 2; j++)
            {
                builder.AddChildren(
                    middle + j,
                    [.. Enumerable.Range(0, 2).Select(_ => new ExploreChild($"file{file}.bin", IsDirectory: false, IsLink: false, Size: 1_000_000L - (++file * 1_000L) + (file == 8 ? extra : 0)))]);
            }
        }

        return builder.Build(order);
    }
}
