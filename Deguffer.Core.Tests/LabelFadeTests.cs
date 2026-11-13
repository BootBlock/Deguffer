using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a name on the map fades as its picture is magnified less than it was drawn at: the text keeps
/// the reader's size while its shape shrinks round it, so it has to be gone by the time it would reach
/// the shape's edge, and whole where its drawing put it.
/// </summary>
public sealed class LabelFadeTests
{
    private const int Width = 800;
    private const int Height = 800;

    private static readonly TileColour Black = new(0, 0, 0);

    /// <summary>
    /// Magnified by the gone point, a left-aligned text 50 wide anchored 190 from its room's right
    /// edge reaches it exactly, and a text 16 high anchored 50 above the room's bottom reaches that.
    /// Each axis is its own: a wide shape does not keep a name in a band that has shrunk.
    /// </summary>
    [Fact]
    public void ATextIsGoneWhereItWouldReachItsShapesEdge()
    {
        var label = Label(x: 110, y: 210, width: 80, centred: false, new LabelRoom(100, 200, 300, 260));

        var (across, down) = label.Fade(textWidth: 50, textHeight: 16);

        Assert.Equal(50f / 190, across.Gone, 5);
        Assert.Equal(16f / 50, down.Gone, 5);
        Assert.Equal(across.Gone * ExploreLabel.FadeRange, across.Whole, 5);
        Assert.Equal(down.Gone * ExploreLabel.FadeRange, down.Whole, 5);
    }

    /// <summary>
    /// A centred text runs half its width each way from its anchor, so the nearer side of its room is
    /// the one it reaches first: 20 of text against 30 of room on the left, not 20 against 100 on the
    /// right, nor its whole width against either.
    /// </summary>
    [Fact]
    public void ACentredTextIsGoneWhereItReachesTheNearerSide()
    {
        var label = Label(x: 100, y: 0, width: 100, centred: true, new LabelRoom(120, -10, 250, 100));

        Assert.Equal((150f, 0f), label.Anchor);
        Assert.Equal(20f / 30, label.Fade(textWidth: 40, textHeight: 10).Across.Gone, 5);
    }

    /// <summary>
    /// A label is whole where its drawing put it, however little room its shape had to spare: its fade
    /// is cut short rather than started before the picture moves.
    /// </summary>
    [Fact]
    public void ALabelWithLittleRoomToSpareIsStillWholeWhereItWasDrawn()
    {
        var label = Label(x: 0, y: 0, width: 90, centred: false, new LabelRoom(0, 0, 100, 100));

        var across = label.Fade(textWidth: 90, textHeight: 10).Across;

        Assert.Equal(0.9f, across.Gone, 5);
        Assert.Equal(ExploreLabel.AtRest, across.Whole);
    }

    /// <summary>
    /// A text already past its room where it was drawn, as a long name at a large text size can be, is
    /// shown there as the layout chose to, and goes as soon as the picture shrinks at all rather than
    /// staying over a neighbour.
    /// </summary>
    [Fact]
    public void ALabelAlreadyPastItsRoomIsWholeOnlyWhereItWasDrawn()
    {
        var label = Label(x: 0, y: 0, width: 50, centred: false, new LabelRoom(0, 0, 50, 100));

        var across = label.Fade(textWidth: 80, textHeight: 10).Across;

        Assert.Equal(ExploreLabel.AtRest, across.Whole);
        Assert.Equal(ExploreLabel.AtRest / ExploreLabel.FadeRange, across.Gone, 5);
    }

    /// <summary>
    /// A picture at rest is shown at a magnification a rounding error under one, because its canvas
    /// is a whole number of device pixels. A name that fills its room to within that rounding, as a
    /// trimmed one does, is whole there all the same: its fade does not run over a sliver below one,
    /// which the rounding alone would be enough to dim it across.
    /// </summary>
    [Fact]
    public void ANameThatJustFillsItsRoomIsWholeAtRest()
    {
        var label = Label(x: 0, y: 0, width: 1000, centred: false, new LabelRoom(0, 0, 1000, 100));

        var across = label.Fade(textWidth: 999.5f, textHeight: 10).Across;

        Assert.True(across.Whole <= 1 - (1f / 4096), $"whole only at {across.Whole}, above where a picture at rest can be shown");
        Assert.True(across.Whole - across.Gone >= 0.1f, $"faded over only {across.Whole - across.Gone}");
    }

    /// <summary>
    /// A name inside a shape fades against the whole of that shape, and a folder's name against its
    /// band alone: the band ends where the folder's contents start, so a folder's name fading as the
    /// picture shrinks never runs down over its first child.
    /// </summary>
    [Fact]
    public void ATreemapNameFadesAgainstItsShapeAndAFoldersAgainstItsBand()
    {
        var tree = NestedTree();
        var limits = LayoutLimits.Default;
        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);
        var surface = new TiledSurface(
            tree, tree.RootNode, Width, Height, limits, ShapeColours.ByBranch(ExploreScheme.Standard), MapStyle.Default, tiles);

        var branch = surface.Labels.Single(label => tree.NameOf(label.Node) == "branch");
        var branchTile = tiles.Single(tile => tile.Node == branch.Node);
        var children = tiles.Where(tile => tile.IsNode && tree.ParentOf(tile.Node) == branch.Node).ToList();

        // A band, or this proves nothing about one.
        Assert.True(branchTile.Header > 0);
        Assert.NotEmpty(children);
        Assert.True(branch.Room.Bottom <= children.Min(tile => tile.Y), "the band's room runs into the folder's contents");
        Assert.True(branch.Room.Bottom - branch.Room.Top >= limits.MinimumLabelHeight);

        var leaves = surface.Labels.Where(label => tree.NameOf(label.Node).StartsWith("leaf", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(leaves);
        Assert.All(leaves, leaf =>
        {
            var tile = tiles.Single(t => t.Node == leaf.Node);

            Assert.Equal(new LabelRoom(tile.X, tile.Y, tile.X + tile.Width, tile.Y + tile.Height), leaf.Room);
        });

        // Every name starts inside its room, or it would be past it where it was drawn.
        Assert.All(surface.Labels, label =>
        {
            Assert.InRange(label.X, label.Room.Left, label.Room.Right);
            Assert.InRange(label.X + label.Width, label.Room.Left, label.Room.Right);
            Assert.InRange(label.Y, label.Room.Top, label.Room.Bottom);
        });
    }

    /// <summary>
    /// A stepped fade is gone no later than the fade it stands for, by under one step, and is whole
    /// where its drawing put the name, so moving a name's fade to its step never leaves it over a
    /// neighbour and never dims it at rest.
    /// </summary>
    [Theory]
    [InlineData(1f / 1024)]
    [InlineData(0.01f)]
    [InlineData(0.2f)]
    [InlineData(0.5f)]
    [InlineData(0.7f)]
    [InlineData(ExploreLabel.AtRest / ExploreLabel.FadeRange)]
    [InlineData(0.9f)]
    [InlineData(0.99f)]
    public void AStepFadesNoLaterThanTheFadeItStandsFor(float gone)
    {
        var fade = new LabelFade(gone, Math.Min(ExploreLabel.AtRest, gone * ExploreLabel.FadeRange));
        var stepped = LabelFade.AtStep(fade.Step);

        Assert.InRange(stepped.Gone, gone, gone * MathF.Pow(2, 1f / LabelFade.StepsPerOctave) * 1.0001f);
        Assert.True(stepped.Whole > stepped.Gone, $"whole at {stepped.Whole}, not above gone at {stepped.Gone}");
        Assert.True(stepped.Whole <= ExploreLabel.AtRest, $"whole only at {stepped.Whole}");
    }

    /// <summary>A fade that starts exactly on a step is that step, and is not rounded to the next one.</summary>
    [Fact]
    public void AFadeOnAStepIsThatStep()
    {
        for (var step = -160; step < 0; step++)
        {
            Assert.Equal(step, LabelFade.AtStep(step).Step);
        }
    }

    /// <summary>
    /// The fades of names whose fades are close together are one step, so a map gives them one
    /// opacity, and names whose fades are a step apart are not.
    /// </summary>
    [Fact]
    public void FadesWithinAStepShareIt()
    {
        var step = LabelFade.AtStep(-20);

        Assert.Equal(-20, new LabelFade(step.Gone * 0.99f, step.Whole).Step);
        Assert.Equal(-20, new LabelFade(step.Gone * 0.97f, step.Whole).Step);
        Assert.Equal(-21, new LabelFade(step.Gone * 0.95f, step.Whole).Step);
    }

    /// <summary>
    /// A name is as opaque as the less opaque of its two axes, none at or below a fade's gone point and
    /// all of it at or above its whole one.
    /// </summary>
    [Fact]
    public void ANameIsAsOpaqueAsItsLessOpaqueAxis()
    {
        var across = new LabelFade(0.4f, 0.5f);
        var down = new LabelFade(0.2f, 0.4f);

        Assert.Equal(1, LabelFade.Opacity<Real>(across, down, 1, 1).Value, 5);
        Assert.Equal(0.5, LabelFade.Opacity<Real>(across, down, 0.45, 1).Value, 5);
        Assert.Equal(0.25, LabelFade.Opacity<Real>(across, down, 1, 0.25).Value, 5);
        Assert.Equal(0.25, LabelFade.Opacity<Real>(across, down, 0.45, 0.25).Value, 5);
        Assert.Equal(0, LabelFade.Opacity<Real>(across, down, 0.3, 1).Value, 5);
    }

    private static ExploreLabel Label(float x, float y, float width, bool centred, LabelRoom room) =>
        new(Node: 1, x, y, width, Rotation: 0, centred, Black, Bytes: 1000, room,
            LabelAxis.Fixed(centred ? x + (width / 2) : x), LabelAxis.Fixed(y));

    private static ExploreTree NestedTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var branch = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("branch", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(
            branch,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"leaf{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
