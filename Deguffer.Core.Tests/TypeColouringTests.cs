using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A map coloured by type: a file painted its own kind, a folder its largest kind, a folder not
/// measured yet painted off the scale, and a legend that names every colour.
/// </summary>
public sealed class TypeColouringTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Videos holds a film and a clip, and is painted as video. Games is painted as the disk image deep
    /// inside it, the virtual machine disk beside them as one, and the root, whose largest kind is
    /// video, as video too.
    /// </summary>
    [Fact]
    public void AFolderIsPaintedItsLargestKindAndAFileItsOwn()
    {
        var tree = TypeBreakdownTests.Tree();
        var colours = ShapeColours.For(tree, ExploreColouring.Type, ExploreScheme.Standard, Now, growth: null, DominantTypes.Measure(tree));
        var surface = Surface(tree, colours);

        var videos = TypeBreakdownTests.Child(tree, tree.RootNode, "Videos");
        var games = TypeBreakdownTests.Child(tree, tree.RootNode, "Games");
        var disk = TypeBreakdownTests.Child(tree, tree.RootNode, "big.vhdx");

        Assert.Equal(TypePalette.For(FileCategory.Video), colours.For(surface, videos, depth: 1));
        Assert.Equal(TypePalette.For(FileCategory.DiskImages), colours.For(surface, games, depth: 1));
        Assert.Equal(TypePalette.For(FileCategory.VirtualMachineDisks), colours.For(surface, disk, depth: 1));
        Assert.Equal(TypePalette.For(FileCategory.Video), colours.For(surface, tree.RootNode, depth: 0));
    }

    /// <summary>
    /// Before the folders are measured a file is still painted its own kind, because its name alone
    /// says it, and a folder is painted as not measured rather than given a kind it may not have. The
    /// kinds of another tree, however alike, describe nothing in this one.
    /// </summary>
    [Fact]
    public void AFolderNotMeasuredIsPaintedOffTheScale()
    {
        var tree = TypeBreakdownTests.Tree();
        var another = TypeBreakdownTests.Tree();
        var videos = TypeBreakdownTests.Child(tree, tree.RootNode, "Videos");
        var image = TypeBreakdownTests.Child(tree, tree.RootNode, "big.vhdx");

        foreach (var types in new[] { null, DominantTypes.Measure(another) })
        {
            var colours = ShapeColours.For(tree, ExploreColouring.Type, ExploreScheme.Standard, Now, growth: null, types);
            var surface = Surface(tree, colours);

            Assert.Equal(TypePalette.Unmeasured, colours.For(surface, videos, depth: 1));
            Assert.Equal(TypePalette.For(FileCategory.VirtualMachineDisks), colours.For(surface, image, depth: 1));
        }
    }

    /// <summary>The colouring reaches the pixels: the same tree drawn by branch and by type differs.</summary>
    [Fact]
    public void ColouringByTypeChangesWhatIsPainted()
    {
        var tree = TypeBreakdownTests.Tree();
        var types = DominantTypes.Measure(tree);

        var byBranch = Painted(tree, ShapeColours.ByBranch(ExploreScheme.Standard));
        var byType = Painted(tree, ShapeColours.For(tree, ExploreColouring.Type, ExploreScheme.Standard, Now, growth: null, types));

        Assert.NotEqual(byBranch, byType);
    }

    /// <summary>
    /// The legend lists every kind once, under the name a picker uses for it, then the band for a folder
    /// not measured yet, so every colour on the map is named beside it.
    /// </summary>
    [Fact]
    public void TheLegendNamesEveryKindAndTheBandOffTheScale()
    {
        Assert.Equal(
            [.. FileCategories.All.Select(FileCategories.Label), "Not measured yet"],
            TypePalette.Bands.Select(band => band.Label));

        Assert.Equal(
            [.. FileCategories.All.Select(TypePalette.For), TypePalette.Unmeasured],
            TypePalette.Bands.Select(band => band.Colour));
    }

    /// <summary>
    /// §6.5: every colour of the legend is told apart from every other, which is what a categorical set
    /// has to do and a set of near neighbours would not. Measured in Oklab, whose distances follow what
    /// the eye sees, against a step a few times the smallest difference anyone notices.
    /// </summary>
    [Fact]
    public void EveryColourOfTheLegendIsToldApart()
    {
        var bands = TypePalette.Bands;

        for (var i = 0; i < bands.Count; i++)
        {
            for (var j = i + 1; j < bands.Count; j++)
            {
                var distance = Distance(bands[i].Colour, bands[j].Colour);

                Assert.True(distance >= 0.08, $"{bands[i].Label} and {bands[j].Label} are {distance:F3} apart.");
            }
        }
    }

    private static ExploreSurface Surface(ExploreTree tree, ShapeColours colours) => ExploreSurface.Create(
        tree, tree.RootNode, ExploreView.Icicle, 200, 200, 1, 1, colours, ExploreSpacing.Comfortable, VolumeSpace.None);

    private static byte[] Painted(ExploreTree tree, ShapeColours colours)
    {
        var surface = Surface(tree, colours);
        var pixels = new byte[PixelBuffer.LengthFor(200, 200)];
        surface.Painter(new TileColour(0, 0, 0)).PaintRegions(pixels);

        return pixels;
    }

    /// <summary>The Euclidean distance between two colours in Oklab (Ottosson, 2020).</summary>
    private static double Distance(TileColour left, TileColour right)
    {
        var (l1, a1, b1) = Oklab(left);
        var (l2, a2, b2) = Oklab(right);

        return Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
    }

    private static (double L, double A, double B) Oklab(TileColour colour)
    {
        var (r, g, b) = (Linear(colour.Red), Linear(colour.Green), Linear(colour.Blue));

        var l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        var m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        var s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));

        return (
            (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s),
            (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s),
            (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
    }

    private static double Linear(byte channel)
    {
        var v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }
}
