using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a card beside the map lights on it (#286): every shape a map coloured by type paints one kind,
/// and no folder whose shape holds other kinds; one folder, by its own shape or the one the screen
/// shows it in; and nothing on a drawing of another tree, or of what was removed since the scan.
/// </summary>
public sealed class MapLightTests
{
    private const int Width = 1200;

    private const int Height = 800;

    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Func<int, bool> NothingGone = _ => false;

    /// <summary>
    /// On a treemap a folder is drawn round what it holds, so a folder of films is not lit as a whole:
    /// its own colour shows only round its files. Each film is, wherever it is, and nothing else.
    /// </summary>
    [Fact]
    public void AKindLightsEveryShapeOfThatKindAndNoFolderDrawnRoundOthers()
    {
        var tree = Media();
        var measured = DominantTypes.Measure(tree);
        var drawing = Draw(tree, ExploreView.Treemap);

        var lit = Nodes(MapLight.Kind(tree, measured, FileCategory.Video).On(drawing, NothingGone));

        // The fixture draws every file one by one, so every film is there to be lit.
        var films = Named(tree, name => name.EndsWith(".mp4", StringComparison.Ordinal));
        Assert.Equal(4, films.Count);
        Assert.All(films, film => Assert.NotEmpty(drawing.Outlines(new HashSet<int> { film })));

        Assert.Equal(films, lit);

        // The folder of films is painted Video, and is drawn, and is still not lit.
        var folder = Find(tree, "films");
        Assert.Equal(FileCategory.Video, DominantTypes.KindOf(tree, folder, measured));
        Assert.NotEmpty(drawing.Outlines(new HashSet<int> { folder }));
        Assert.DoesNotContain(folder, lit);
    }

    /// <summary>
    /// An icicle draws what a folder holds beside it rather than inside it, so a folder painted the kind
    /// shows its own colour, and is lit with its films.
    /// </summary>
    [Fact]
    public void AFolderWithNothingDrawnInsideItIsLitByItsKind()
    {
        var tree = Media();
        var measured = DominantTypes.Measure(tree);
        var films = Find(tree, "films");

        var icicle = Nodes(MapLight.Kind(tree, measured, FileCategory.Video).On(Draw(tree, ExploreView.Icicle), NothingGone));
        var sunburst = Nodes(MapLight.Kind(tree, measured, FileCategory.Video).On(Draw(tree, ExploreView.Sunburst), NothingGone));

        Assert.Contains(films, icicle);
        Assert.Contains(films, sunburst);
        Assert.DoesNotContain(Find(tree, "photos"), icicle);
        Assert.DoesNotContain(Find(tree, "photos"), sunburst);
    }

    [Fact]
    public void AFolderIsLitByItsOwnShape()
    {
        var tree = Media();
        var films = Find(tree, "films");

        var outline = Assert.Single(Lit(MapLight.Folder(tree, films).On(Draw(tree, ExploreView.Treemap), NothingGone)));

        Assert.Equal(films, outline.Node);
    }

    /// <summary>A folder too deep to be drawn on its own is lit where the screen shows it: the deepest folder above it that is drawn.</summary>
    [Fact]
    public void AFolderNotDrawnIsLitByTheDeepestFolderAboveItThatIs()
    {
        var tree = Chain(depth: 40);
        var drawing = Draw(tree, ExploreView.Treemap);
        var deepest = Find(tree, "level39");

        Assert.Empty(drawing.Outlines(new HashSet<int> { deepest }));

        var drawn = Enumerable.Range(0, 40)
            .Select(level => Find(tree, $"level{level}"))
            .Last(node => drawing.Outlines(new HashSet<int> { node }).Count > 0);

        var outline = Assert.Single(Lit(MapLight.Folder(tree, deepest).On(drawing, NothingGone)));

        Assert.NotEqual(Find(tree, "level0"), drawn);
        Assert.Equal(drawn, outline.Node);
    }

    /// <summary>The same numbers in another tree are other shapes, so a light lights nothing there (§7.1).</summary>
    [Fact]
    public void ALightOfOneTreeLightsNothingOnAnother()
    {
        var tree = Media();
        var other = Media();
        var drawing = Draw(other, ExploreView.Treemap);

        Assert.NotEmpty(Lit(MapLight.Folder(other, Find(other, "films")).On(drawing, NothingGone)));
        Assert.Null(MapLight.Folder(tree, Find(tree, "films")).On(drawing, NothingGone));
        Assert.Null(MapLight.Kind(tree, DominantTypes.Measure(tree), FileCategory.Video).On(drawing, NothingGone));
    }

    [Fact]
    public void WhatWasRemovedSinceTheScanIsNotLit()
    {
        var tree = Media();
        var drawing = Draw(tree, ExploreView.Treemap);
        var films = Find(tree, "films");
        var removed = Find(tree, "film0.mp4");

        var lit = Nodes(MapLight.Kind(tree, DominantTypes.Measure(tree), FileCategory.Video).On(drawing, node => node == removed));

        Assert.DoesNotContain(removed, lit);
        Assert.Contains(Find(tree, "film1.mp4"), lit);
        Assert.Empty(Lit(MapLight.Folder(tree, films).On(drawing, node => node == films)));
    }

    private static HashSet<int> Nodes(IReadOnlyList<ExploreOutline>? outlines) => [.. Lit(outlines).Select(outline => outline.Node)];

    /// <summary>What a light lit on a drawing of its own tree, which always has an answer.</summary>
    private static IReadOnlyList<ExploreOutline> Lit(IReadOnlyList<ExploreOutline>? outlines)
    {
        Assert.NotNull(outlines);

        return outlines;
    }

    private static ExploreSurface Draw(ExploreTree tree, ExploreView view) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ExploreColouring.Type, ExploreScheme.Standard, Now, ExploreSpacing.Comfortable, VolumeSpace.None);

    /// <summary>
    /// Three films and some notes in one folder, photos in another, and a fourth film beside a document
    /// in a third, all large enough to be drawn one by one.
    /// </summary>
    private static ExploreTree Media()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("films", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("photos", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("mixed", IsDirectory: true, IsLink: false, Size: 0),
            ]);

        builder.AddChildren(first,
        [
            new ExploreChild("film0.mp4", IsDirectory: false, IsLink: false, Size: 900_000),
            new ExploreChild("film1.mp4", IsDirectory: false, IsLink: false, Size: 800_000),
            new ExploreChild("film2.mp4", IsDirectory: false, IsLink: false, Size: 700_000),
            new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 300_000),
        ]);

        builder.AddChildren(first + 1,
        [
            new ExploreChild("photo0.jpg", IsDirectory: false, IsLink: false, Size: 600_000),
            new ExploreChild("photo1.jpg", IsDirectory: false, IsLink: false, Size: 500_000),
        ]);

        builder.AddChildren(first + 2,
        [
            new ExploreChild("report.docx", IsDirectory: false, IsLink: false, Size: 600_000),
            new ExploreChild("clip.mp4", IsDirectory: false, IsLink: false, Size: 400_000),
        ]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A chain of <paramref name="depth"/> folders, one inside the next, each beside a file as large
    /// as the rest of the chain, so every level halves the room and the last ones are too small to draw.
    /// </summary>
    private static ExploreTree Chain(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var level = 0; level < depth; level++)
        {
            parent = builder.AddChildren(
                parent,
                [
                    new ExploreChild($"level{level}", IsDirectory: true, IsLink: false, Size: 0),
                    new ExploreChild($"beside{level}", IsDirectory: false, IsLink: false, Size: 1L << (depth - level)),
                ]);
        }

        builder.AddChildren(parent, [new ExploreChild("last", IsDirectory: false, IsLink: false, Size: 2)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Find(ExploreTree tree, string name) =>
        Named(tree, candidate => candidate == name).Single();

    private static HashSet<int> Named(ExploreTree tree, Func<string, bool> name)
    {
        var found = new HashSet<int>();
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            if (name(tree.NameOf(node)))
            {
                found.Add(node);
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        return found;
    }
}
