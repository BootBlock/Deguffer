using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Explore page shows round its picture: which panels are open, which colours the legend is
/// in, what is measured for them, and the sentence that names a view the page could not draw.
/// </summary>
public sealed class ExploreDisplayTests
{
    private static readonly ExploreTree Finished = Tree(ExploreChildOrder.BySize);

    /// <summary>A tree a running scan published, in name order, which no treemap or sunburst can be drawn from.</summary>
    private static readonly ExploreTree Partial = Tree(ExploreChildOrder.ByName, snapshot: true);

    /// <summary>A tree a running scan published by size, as it does while the treemap or the sunburst is on screen.</summary>
    private static readonly ExploreTree PartialBySize = Tree(ExploreChildOrder.BySize, snapshot: true);

    /// <summary>A tree ordered by name that no scan is still filling in.</summary>
    private static readonly ExploreTree FinishedByName = Tree(ExploreChildOrder.ByName);

    /// <summary>
    /// The legend is on screen only where the colours are a scale, the picture is a map rather than a
    /// list, something has been scanned into it, and the map is not in a high contrast theme's two
    /// colours.
    /// </summary>
    [Fact]
    public void TheLegendShowsOnlyBesideAMapColouredByAScale()
    {
        Assert.True(Display(Finished, ExploreView.Treemap, ExploreColouring.Age).ShowsLegend);
        Assert.True(Display(Finished, ExploreView.Icicle, ExploreColouring.Growth).ShowsLegend);
        Assert.True(Display(Finished, ExploreView.Tree, ExploreColouring.Depth).ShowsLegend);

        Assert.False(Display(Finished, ExploreView.Treemap, ExploreColouring.Branch).ShowsLegend);
        Assert.False(Display(Finished, ExploreView.List, ExploreColouring.Age).ShowsLegend);
        Assert.False(Display(Finished, ExploreView.Files, ExploreColouring.Age).ShowsLegend);
        Assert.False(Display(null, ExploreView.Treemap, ExploreColouring.Age).ShowsLegend);
        Assert.False(Display(Finished, ExploreView.Treemap, ExploreColouring.Age, inContrast: true).ShowsLegend);
    }

    /// <summary>
    /// The legend is the one <see cref="MapLegend"/> gives, in the scheme of the picture asked for:
    /// each picture keeps its own colours, so a change of view is a change of scheme.
    /// </summary>
    [Fact]
    public void TheLegendIsInTheSchemeOfThePictureOnScreen()
    {
        var look = MapLook.Default with { Treemap = ExploreScheme.Vivid, Icicle = ExploreScheme.Deep };

        var treemap = Display(Finished, ExploreView.Treemap, ExploreColouring.Age, look: look);
        var icicle = Display(Finished, ExploreView.Icicle, ExploreColouring.Age, look: look);
        var listed = Display(Finished, ExploreView.List, ExploreColouring.Age, look: look);

        Assert.Equal(ExploreScheme.Vivid, treemap.Scheme);
        Assert.Equal(ExploreScheme.Deep, icicle.Scheme);
        Assert.Equal(ExploreScheme.Vivid, listed.Scheme);
        Assert.Same(MapLegend.For(ExploreColouring.Age, ExploreScheme.Deep), icicle.Legend);
    }

    /// <summary>
    /// The panels of what grew and of what kind of file fills the folder are open with their colouring
    /// in every view, the lists included, once something has been scanned. The Files layout's filters
    /// are open with that layout.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.List)]
    [InlineData(ExploreView.Files)]
    public void EachPanelIsOpenWithWhatItExplains(ExploreView view)
    {
        Assert.True(Display(Finished, view, ExploreColouring.Growth).ShowsGrowth);
        Assert.False(Display(Finished, view, ExploreColouring.Growth).ShowsTypes);
        Assert.True(Display(Finished, view, ExploreColouring.Type).ShowsTypes);
        Assert.False(Display(Finished, view, ExploreColouring.Type).ShowsGrowth);
        Assert.Equal(view == ExploreView.Files, Display(Finished, view, ExploreColouring.Branch).ShowsFileFilters);

        Assert.False(Display(null, view, ExploreColouring.Growth).ShowsGrowth);
        Assert.False(Display(null, view, ExploreColouring.Type).ShowsTypes);
        Assert.False(Display(null, ExploreView.Files, ExploreColouring.Branch).ShowsFileFilters);
    }

    /// <summary>
    /// The largest files are searched for only in the Files layout, and kinds of file are measured
    /// only for a map coloured by type, before anything has been scanned as well as after, so each
    /// starts as the scan's first tree arrives.
    /// </summary>
    [Fact]
    public void TheFilesAreSearchedAndTheTypesMeasuredOnlyForWhatShowsThem()
    {
        Assert.True(Display(null, ExploreView.Files, ExploreColouring.Branch).SearchesFiles);
        Assert.False(Display(Finished, ExploreView.List, ExploreColouring.Branch).SearchesFiles);
        Assert.True(Display(null, ExploreView.List, ExploreColouring.Type).MeasuresTypes);
        Assert.False(Display(Finished, ExploreView.List, ExploreColouring.Growth).MeasuresTypes);
    }

    /// <summary>
    /// The folder is listed a row per entry only in the List layout, before anything has been
    /// scanned as well as after, so the rows are written as the scan's first tree arrives. The
    /// pictures and the Files layout read none of them.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap)]
    [InlineData(ExploreView.Icicle)]
    [InlineData(ExploreView.Sunburst)]
    [InlineData(ExploreView.Tree)]
    [InlineData(ExploreView.Files)]
    public void TheFolderIsListedOnlyInTheListLayout(ExploreView view)
    {
        Assert.True(Display(null, ExploreView.List, ExploreColouring.Branch).ListsRows);
        Assert.True(Display(Partial, ExploreView.List, ExploreColouring.Type).ListsRows);
        Assert.False(Display(Finished, view, ExploreColouring.Branch).ListsRows);
        Assert.False(Display(null, view, ExploreColouring.Branch).ListsRows);
    }

    /// <summary>
    /// Only a treemap drawn as one zooms, so only it offers the line naming its mouse controls. A
    /// running scan draws the icicle whatever was asked for.
    /// </summary>
    [Fact]
    public void OnlyATreemapDrawnAsOneZooms()
    {
        Assert.True(Display(Finished, ExploreView.Treemap, ExploreColouring.Branch).Zooms);
        Assert.False(Display(Partial, ExploreView.Treemap, ExploreColouring.Branch).Zooms);
        Assert.False(Display(Finished, ExploreView.Sunburst, ExploreColouring.Branch).Zooms);
        Assert.False(Display(Finished, ExploreView.List, ExploreColouring.Branch).Zooms);
        Assert.False(Display(null, ExploreView.Treemap, ExploreColouring.Branch).Zooms);
    }

    /// <summary>
    /// A snapshot is named for what it does to each view, and a finished tree, or none, says
    /// nothing, whatever order it is in.
    /// </summary>
    [Fact]
    public void ASubstitutedViewIsNamed()
    {
        Assert.EndsWith("ordered by size for the treemap.", Display(Partial, ExploreView.Treemap, ExploreColouring.Branch).Note);
        Assert.EndsWith("ordered by size for the sunburst.", Display(Partial, ExploreView.Sunburst, ExploreColouring.Branch).Note);
        Assert.StartsWith("The largest files found so far", Display(Partial, ExploreView.Files, ExploreColouring.Branch).Note);
        Assert.StartsWith("In name order while the scan runs", Display(Partial, ExploreView.List, ExploreColouring.Branch).Note);
        Assert.StartsWith("In name order while the scan runs", Display(Partial, ExploreView.Icicle, ExploreColouring.Branch).Note);

        Assert.Null(Display(Finished, ExploreView.Treemap, ExploreColouring.Branch).Note);
        Assert.Null(Display(FinishedByName, ExploreView.List, ExploreColouring.Branch).Note);
        Assert.Null(Display(null, ExploreView.Treemap, ExploreColouring.Branch).Note);
    }

    /// <summary>
    /// A snapshot that came by size, because the treemap or the sunburst was on screen, draws the view
    /// asked for, so a picture says nothing about it. The Files layout still says a larger file may
    /// turn up, and a list in size order has nothing to own up to.
    /// </summary>
    [Fact]
    public void ASnapshotBySizeDrawsThePictureAskedFor()
    {
        Assert.Null(Display(PartialBySize, ExploreView.Treemap, ExploreColouring.Branch).Note);
        Assert.Null(Display(PartialBySize, ExploreView.Sunburst, ExploreColouring.Branch).Note);
        Assert.Null(Display(PartialBySize, ExploreView.List, ExploreColouring.Branch).Note);
        Assert.StartsWith("The largest files found so far", Display(PartialBySize, ExploreView.Files, ExploreColouring.Branch).Note);
    }

    /// <summary>
    /// The panel of broken shortcuts in the Start menu opens only while the Files layout lists broken
    /// links over a scan, and takes the side panel's place from what grew and from what kind of file.
    /// </summary>
    [Theory]
    [InlineData(ExploreColouring.Growth)]
    [InlineData(ExploreColouring.Type)]
    public void ThePanelOfShortcutFoldersOpensOnlyBesideTheBrokenLinks(ExploreColouring colouring)
    {
        var links = Display(Finished, ExploreView.Files, colouring, listing: FileListing.BrokenLinks);

        Assert.True(links.ShowsShortcutFolders);
        Assert.False(links.ShowsGrowth);
        Assert.False(links.ShowsTypes);

        Assert.False(Display(Finished, ExploreView.Files, colouring).ShowsShortcutFolders);
        Assert.False(Display(Finished, ExploreView.List, colouring, listing: FileListing.BrokenLinks).ShowsShortcutFolders);
        Assert.False(Display(null, ExploreView.Files, colouring, listing: FileListing.BrokenLinks).ShowsShortcutFolders);
        Assert.True(Display(Finished, ExploreView.List, colouring, listing: FileListing.BrokenLinks).ShowsGrowth
            || Display(Finished, ExploreView.List, colouring, listing: FileListing.BrokenLinks).ShowsTypes);
    }

    private static ExploreDisplay Display(
        ExploreTree? tree,
        ExploreView view,
        ExploreColouring colouring,
        bool inContrast = false,
        MapLook? look = null,
        FileListing listing = FileListing.LargestFiles) =>
        new(tree, view, colouring, look ?? MapLook.Default, inContrast, listing);

    private static ExploreTree Tree(ExploreChildOrder order, bool snapshot = false)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("Users", IsDirectory: true, IsLink: false, Size: 2_000),
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 1_000),
        ]);

        return snapshot ? builder.Snapshot(order) : builder.Build(order);
    }
}
