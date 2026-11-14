using System.Globalization;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How much of a tree a map draws and how its names read: the defaults are the limits the map always
/// had, each choice changes the drawing it names and nothing else, and a file written before the
/// choices existed, or holding values no choice names, reads with the defaults.
/// </summary>
public sealed class MapDetailTests
{
    /// <summary>A map nobody has tuned draws with the limits it always had.</summary>
    [Fact]
    public void TheDefaultDetailIsTheDetailTheMapAlwaysHad()
    {
        Assert.Equal(LayoutLimits.Default, LayoutLimits.Default.Detailed(MapDetail.Default));
        Assert.Equal(1.25, MapDetail.Default.TextScaleFor(MapLettering.Standard, 1.25));
        Assert.Equal(64, MapDetail.Default.MostNames);
        Assert.True(MapDetail.Default.Sizes);
    }

    /// <summary>Each step draws a finer shape and opens more levels than the one before it.</summary>
    [Fact]
    public void EachStepDrawsFinerShapesAndOpensMoreLevels()
    {
        var smallest = Enum.GetValues<MapSmallestShape>().Select(shape => new MapDetail(SmallestShape: shape).SmallestShapeInDips).ToList();
        var levels = Enum.GetValues<MapLevels>().Select(level => new MapDetail(Levels: level).LevelCount).ToList();
        var names = Enum.GetValues<MapNameCount>().Select(count => new MapDetail(Names: count).MostNames).ToList();

        Assert.Equal(smallest.OrderDescending(), smallest);
        Assert.Equal(levels.Order(), levels);
        Assert.Equal(names.Order(), names);
        Assert.Equal(smallest.Count, smallest.Distinct().Count());
        Assert.Equal(levels.Count, levels.Distinct().Count());
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    /// <summary>
    /// The text size multiplies the reader's Windows text size for the application's lettering, and
    /// leaves SpaceMonger's at the one size its pixel type is drawn at.
    /// </summary>
    [Fact]
    public void TheTextSizeIsTheApplicationsLetteringsAlone()
    {
        var large = new MapDetail(TextSize: MapTextSize.Large);

        Assert.Equal(1.5 * 1.2, large.TextScaleFor(MapLettering.Standard, 1.5), 6);
        Assert.Equal(1.5, large.TextScaleFor(MapLettering.Classic, 1.5));
        Assert.True(new MapDetail(TextSize: MapTextSize.Small).TextScaleFor(MapLettering.Standard, 1) < 1);
    }

    /// <summary>A finer smallest shape draws more shapes, and a larger one fewer, of the same tree on the same canvas.</summary>
    [Fact]
    public void AFinerSmallestShapeDrawsMoreShapes()
    {
        var tree = ManySizes();

        int Shapes(MapSmallestShape shape) => Draw(tree, new MapDetail(SmallestShape: shape)).Tiles.Count(tile => tile.IsNode);

        Assert.True(Shapes(MapSmallestShape.Large) < Shapes(MapSmallestShape.Standard));
        Assert.True(Shapes(MapSmallestShape.Standard) < Shapes(MapSmallestShape.Tiny));
    }

    /// <summary>Fewer levels stop the treemap higher up a deep tree, and more take it further down.</summary>
    [Fact]
    public void TheLevelsChosenAreHowDeepTheTreemapGoes()
    {
        var tree = Deep(14);

        int Deepest(MapLevels levels) => Draw(tree, new MapDetail(Levels: levels)).Tiles.Max(tile => tile.Depth);

        Assert.True(Deepest(MapLevels.Fewer) <= 4);
        Assert.True(Deepest(MapLevels.Fewer) < Deepest(MapLevels.Standard));
        Assert.True(Deepest(MapLevels.Standard) < Deepest(MapLevels.Most));
    }

    /// <summary>A drawing names no more shapes than the reader asked for, and names more when asked for more.</summary>
    [Fact]
    public void ADrawingNamesAsManyShapesAsAskedForAndNoMore()
    {
        var tree = Wide(300);

        // Inside the shapes: the folder the drawing opens on is named in a band of its own, whatever the count.
        int Named(MapNameCount count) =>
            Draw(tree, new MapDetail(Names: count), 2400, 1600).Labels.Count(label => label.Node != ExploreTreeBuilder.RootNode);

        Assert.Equal(24, Named(MapNameCount.Fewer));
        Assert.Equal(64, Named(MapNameCount.Standard));
        Assert.Equal(160, Named(MapNameCount.More));
    }

    /// <summary>
    /// A drawing carries the text size its labels' room was measured at, so the names are set at it,
    /// and whether each is followed by its size.
    /// </summary>
    [Fact]
    public void ADrawingCarriesItsTextSizeAndWhetherSizesAreWritten()
    {
        var drawing = Draw(Wide(20), new MapDetail(TextSize: MapTextSize.Larger, Sizes: false), textScale: 1.25);

        Assert.Equal(1.25 * 1.4, drawing.TextScale, 6);
        Assert.False(drawing.Sizes);
        Assert.Equal(1, Draw(Wide(20), MapDetail.Default).TextScale);
    }

    /// <summary>
    /// A caption without sizes is the name alone in the application's lettering, and in SpaceMonger's a
    /// name and its date, the date taking the place the bytes had.
    /// </summary>
    [Fact]
    public void ACaptionWithoutSizesLeavesTheSizeOut()
    {
        var written = new DateTime(2026, 11, 7, 16, 53, 10);
        var caption = new MapCaption("pagefile.sys", "50.0 GB", 53_687_091_200, written);
        var date = written.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);

        Assert.Equal("pagefile.sys", caption.Text(MapLettering.Standard, 1, sizes: false));
        Assert.Equal($"pagefile.sys\n{date}", caption.Text(MapLettering.Classic, 2, sizes: false));
        Assert.Equal($"pagefile.sys\n{date}", caption.Text(MapLettering.Classic, 3, sizes: false));
        Assert.Equal("pagefile.sys", caption.Text(MapLettering.Classic, 1, sizes: false));
    }

    /// <summary>A file written before the detail could be chosen reads with the detail the map always had.</summary>
    [Fact]
    public void AFileFromBeforeTheChoiceReadsWithTheDefaultDetail() =>
        Assert.Equal(MapDetail.Default, LoadWritten("""{ "Theme": "Dark" }""").MapDetail);

    /// <summary>A value no choice names, as a hand-edited file can hold, reads as the default, and the rest of the group is kept.</summary>
    [Fact]
    public void AStrayValueReadsAsTheDefault() =>
        Assert.Equal(
            new MapDetail(Levels: MapLevels.Most, Sizes: false),
            MapLook.From(LoadWritten("""{ "MapDetail": { "SmallestShape": 7, "Levels": "Most", "TextSize": -3, "Names": 99, "Sizes": false } }""")).Detail);

    /// <summary>Every choice is written by name, read back as it was written, and nothing derived from them is written beside them.</summary>
    [Fact]
    public void TheDetailIsWrittenAndReadBackByName()
    {
        using var temp = new TempDirectory();
        var store = new PreferenceStore(new FakeUserEnvironment(temp.Path));
        var detail = new MapDetail(MapSmallestShape.Tiny, MapLevels.More, MapTextSize.Larger, MapNameCount.Most, Sizes: false);

        Assert.True(store.Save(AppPreferences.Default with { MapDetail = detail }));

        var written = File.ReadAllText(Path.Combine(new FakeUserEnvironment(temp.Path).LocalAppData, "Deguffer", "preferences.json"));

        Assert.Equal(detail, store.Load().MapDetail);
        Assert.Contains("\"Tiny\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Larger\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MapDetail.SmallestShapeInDips), written, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MapDetail.MostNames), written, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(MapStyle.Outlined), written, StringComparison.Ordinal);
    }

    private static TiledSurface Draw(ExploreTree tree, MapDetail detail, int width = 800, int height = 600, double textScale = 1) =>
        (TiledSurface)ExploreSurface.Create(
            tree, ExploreTreeBuilder.RootNode, ExploreView.Treemap, width, height, scale: 1, textScale,
            ShapeColours.ByDepth, ExploreSpacing.Comfortable, VolumeSpace.None, detail: detail);

    /// <summary>Files from large to very small, so where the smallest shape is drawn decides how many are.</summary>
    private static ExploreTree ManySizes()
    {
        var builder = new ExploreTreeBuilder("Sample");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(1, 600).Select(i => new ExploreChild($"f{i}.bin", IsDirectory: false, IsLink: false, Size: 40_000_000L / (i * i)))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary><paramref name="levels"/> folders, each in the one before, each beside a file as large as what it holds.</summary>
    private static ExploreTree Deep(int levels)
    {
        var builder = new ExploreTreeBuilder("Sample");
        var folder = ExploreTreeBuilder.RootNode;

        for (var level = 0; level < levels; level++)
        {
            var size = 1L << (2 * (levels - level));
            folder = builder.AddChildren(
                folder,
                [
                    new ExploreChild($"folder{level}", IsDirectory: true, IsLink: false, Size: 0),
                    new ExploreChild($"file{level}.bin", IsDirectory: false, IsLink: false, Size: size),
                ]);
        }

        builder.AddChildren(folder, [new ExploreChild("last.bin", IsDirectory: false, IsLink: false, Size: 4)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary><paramref name="count"/> files of one size, each with room for its name on a large canvas.</summary>
    private static ExploreTree Wide(int count)
    {
        var builder = new ExploreTreeBuilder("Sample");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(1, count).Select(i => new ExploreChild($"f{i}.bin", IsDirectory: false, IsLink: false, Size: 1_000_000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static AppPreferences LoadWritten(string json)
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(Path.Combine(directory.FullName, "preferences.json"), json);

        return new PreferenceStore(environment).Load();
    }
}
