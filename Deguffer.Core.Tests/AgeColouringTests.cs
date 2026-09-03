using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The second thing a colour on the map can say: how long ago the shape was last written.
///
/// <para>Tested against a fixed instant rather than the clock, because a banding driven by
/// <see cref="DateTime.UtcNow"/> is a test that changes its mind overnight.</para>
/// </summary>
public class AgeColouringTests
{
    private const int Width = 200;
    private const int Height = 200;

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(3, "This week")]
    [InlineData(20, "This month")]
    [InlineData(200, "This year")]
    [InlineData(500, "1 to 2 years")]
    [InlineData(1000, "2 to 5 years")]
    [InlineData(4000, "Over 5 years")]
    public void EachAgeFallsInTheBandThatNamesIt(int daysAgo, string expected) =>
        Assert.Equal(expected, AgePalette.BandOf(Written(daysAgo), Now).Label);

    /// <summary>
    /// The band boundaries themselves, which is where an off-by-one lives. A file written exactly
    /// seven days ago is not "this week" — the band covers what is written <em>within</em> its
    /// count of days, and the day it names is where the next band starts.
    /// </summary>
    [Fact]
    public void ABoundaryBelongsToTheOlderBand()
    {
        Assert.Equal("This week", AgePalette.BandOf(Written(6), Now).Label);
        Assert.Equal("This month", AgePalette.BandOf(Written(7), Now).Label);

        Assert.Equal("This year", AgePalette.BandOf(Written(364), Now).Label);
        Assert.Equal("1 to 2 years", AgePalette.BandOf(Written(365), Now).Label);
    }

    /// <summary>
    /// An entry nothing could date must not be painted as though it were ancient. That is the one
    /// reading of this picture that could get something deleted, and it is the same rule
    /// <see cref="Scanning.RelativeAge"/> holds for the sentence.
    /// </summary>
    [Fact]
    public void AnUndatedEntryGetsItsOwnColourAndNotTheOldestOne()
    {
        var unknown = AgePalette.BandOf(ExploreTimestamp.Unknown, Now);
        var oldest = AgePalette.BandOf(Written(20_000), Now);

        Assert.Equal("Not known", unknown.Label);
        Assert.NotEqual(oldest.Colour, unknown.Colour);

        // And it is not any of the dated colours either, so it cannot be mistaken for a step of the
        // scale it is sitting next to.
        Assert.DoesNotContain(
            unknown.Colour,
            AgePalette.Bands.Where(b => b.Label != "Not known").Select(b => b.Colour));
    }

    /// <summary>
    /// A file written during the scan, and a clock that disagrees with the filesystem's, both
    /// produce a date in the future. Neither is an age, and the newest band is the only reading
    /// that is both honest and safe — the alternative is a negative age falling through every
    /// threshold to "over 5 years".
    /// </summary>
    [Fact]
    public void ADateInTheFutureIsDrawnAsTheNewest() =>
        Assert.Equal("Today", AgePalette.BandOf(Written(-30), Now).Label);

    /// <summary>
    /// Every band is a different colour. A ramp with a repeat in it reads as two ages that are the
    /// same age, which is worse than a shorter ramp would be.
    /// </summary>
    [Fact]
    public void NoTwoBandsShareAColour() =>
        Assert.Equal(AgePalette.Bands.Count, AgePalette.Bands.Select(b => b.Colour).Distinct().Count());

    /// <summary>
    /// The ramp has to be readable as an order, which is what a categorical palette cannot do. Its
    /// lightness moves the same way at every step, so which of two shapes is older can be read off
    /// the picture without consulting the legend — and it survives being seen in grey.
    /// </summary>
    [Fact]
    public void TheRampGetsDarkerWithEveryStepBackwards()
    {
        var dated = AgePalette.Bands.Where(b => b.Label != "Not known").ToList();

        for (var i = 1; i < dated.Count; i++)
        {
            Assert.True(
                dated[i].Colour.RelativeLuminance < dated[i - 1].Colour.RelativeLuminance,
                $"'{dated[i].Label}' is not darker than '{dated[i - 1].Label}'.");
        }
    }

    /// <summary>
    /// The colouring actually reaches the pixels, and it is the only thing that changed: the same
    /// tree drawn the same way twice differs when — and only when — the colouring does.
    ///
    /// <para>Two children of very different ages, so a picture painted by branch and one painted by
    /// age cannot coincide. Asserted on the bytes rather than on a palette lookup, because the
    /// question here is whether the choice survives the whole path from the surface to the buffer.
    /// </para>
    /// </summary>
    [Fact]
    public void ColouringByAgeChangesWhatIsPainted()
    {
        var tree = TwoChildrenOfDifferentAges();

        var byBranch = Painted(tree, ExploreColouring.Branch);
        var byAge = Painted(tree, ExploreColouring.Age);

        Assert.NotEqual(byBranch, byAge);

        // And the same choice twice is the same picture, so the difference above is the colouring
        // rather than anything incidental to painting twice.
        Assert.Equal(byAge, Painted(tree, ExploreColouring.Age));
    }

    /// <summary>
    /// Two shapes of the same age are the same colour whatever branch they are in, which is the
    /// whole claim an age-coloured map makes. Under the branch colouring they are different
    /// colours, and that contrast is what stops this passing against a surface that ignored the
    /// setting.
    /// </summary>
    [Fact]
    public void TwoBranchesOfOneAgeArePaintedAlike()
    {
        var tree = TwoChildrenOfTheSameAge();

        Assert.Single(ColoursOfTheChildren(tree, ExploreColouring.Age));
        Assert.Equal(2, ColoursOfTheChildren(tree, ExploreColouring.Branch).Count);
    }

    /// <summary>
    /// A label has to contrast with the colour its own shape was actually painted, not with the
    /// colour the other scheme would have used. Getting this wrong is invisible in the branch
    /// colouring, where the two agree by construction, and illegible in the age colouring.
    /// </summary>
    [Fact]
    public void LabelsContrastWithTheColourTheirShapeWasPainted()
    {
        var tree = TwoChildrenOfDifferentAges();

        var surface = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Icicle, Width, Height, scale: 1, ExploreColouring.Age, Now);

        Assert.NotEmpty(surface.Labels);

        foreach (var label in surface.Labels)
        {
            var painted = AgePalette.For(tree.ModifiedOf(label.Node), Now);

            Assert.Equal(painted.ContrastingText, label.Colour);
        }
    }

    private static ExploreTimestamp Written(int daysAgo) =>
        ExploreTimestamp.FromUtc(Now.AddDays(-daysAgo));

    /// <summary>
    /// The distinct colours the two children were painted, read back off the canvas.
    ///
    /// <para>Sampled from the pixels rather than asked of the palette, so this fails if the choice
    /// is dropped anywhere between the surface and the buffer. The icicle is used because it puts
    /// the children in fixed, separate bands rather than packing them by size.</para>
    /// </summary>
    private static IReadOnlyCollection<TileColour> ColoursOfTheChildren(
        ExploreTree tree, ExploreColouring colouring)
    {
        var pixels = Painted(tree, colouring);

        // The second row of shapes: the root occupies the top band, and the two children the one
        // below it, one in each half.
        return [.. new[] { At(pixels, Width / 4, 30), At(pixels, Width * 3 / 4, 30) }.Distinct()];
    }

    private static byte[] Painted(ExploreTree tree, ExploreColouring colouring)
    {
        var surface = ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Icicle, Width, Height, scale: 1, colouring, Now);

        var pixels = new byte[PixelBuffer.LengthFor(Width, Height)];
        surface.Paint(pixels, new TileColour(0, 0, 0));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int x, int y)
    {
        var offset = ((y * Width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static ExploreTree TwoChildrenOfDifferentAges() => Tree(daysAgo: 0, otherDaysAgo: 4000);

    private static ExploreTree TwoChildrenOfTheSameAge() => Tree(daysAgo: 4000, otherDaysAgo: 4000);

    private static ExploreTree Tree(int daysAgo, int otherDaysAgo)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("recent", IsDirectory: false, IsLink: false, 1000, Written(daysAgo), Written(daysAgo)),
                new ExploreChild(
                    "stale", IsDirectory: false, IsLink: false, 1000, Written(otherDaysAgo), Written(otherDaysAgo)),
            ]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
