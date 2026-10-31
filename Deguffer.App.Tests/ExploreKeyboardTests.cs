using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Tests;

/// <summary>
/// How the map's keyboard wires <see cref="MapFocusEntry"/>'s rules to the drawing on screen: every way
/// onto a shape lands once, on the shape that drawing shows, and the menu key after a click lands on
/// what the click picked or not at all (§7.1). The rules themselves are tested in Core.
/// </summary>
public sealed class ExploreKeyboardTests
{
    private const int Width = 300;
    private const int Height = 100;

    /// <summary>Four files under the root, of which the drawing draws only the first three, side by side.</summary>
    private static readonly ExploreTree Tree = Files(4);

    private static readonly TiledSurface Drawing = new(
        Tree,
        Tree.RootNode,
        Width,
        Height,
        LayoutLimits.Default,
        ShapeColours.ByBranch(ExploreScheme.Standard),
        MapStyle.Default,
        [
            new ExploreTile(Tree.RootNode, Depth: 0, Bytes: 4000, X: 0, Y: 0, Width: Width, Height: Height),
            new ExploreTile(1, Depth: 1, Bytes: 1000, X: 0, Y: 0, Width: 100, Height: Height),
            new ExploreTile(2, Depth: 1, Bytes: 1000, X: 100, Y: 0, Width: 100, Height: Height),
            new ExploreTile(3, Depth: 1, Bytes: 1000, X: 200, Y: 0, Width: 100, Height: Height),
        ]);

    /// <summary>The first arrow after a click goes on from what the click picked, and picks the shape it reaches.</summary>
    [Fact]
    public void TheFirstArrowAfterAClickGoesOnFromWhatItPicked()
    {
        var (keyboard, landings) = Keyboard(picked: 1);

        Assert.True(keyboard.Arrow(Drawing, MapDirection.Right));

        var landing = Assert.Single(landings);
        Assert.Equal(2, landing.Shape.Node);
        Assert.True(landing.Picks);
        Assert.Same(Drawing, landing.Drawing);
        Assert.True(keyboard.IsActive);
        Assert.Equal((null, 2), keyboard.On);
    }

    /// <summary>The first arrow onto a map nothing is picked on lands on the first shape and picks nothing.</summary>
    [Fact]
    public void TheFirstArrowOntoAMapNothingIsPickedOnMarksTheFirstShape()
    {
        var (keyboard, landings) = Keyboard(picked: null);

        Assert.True(keyboard.Arrow(Drawing, MapDirection.Right));

        var landing = Assert.Single(landings);
        Assert.Equal(Drawing.Shapes(null)[0].Node, landing.Shape.Node);
        Assert.False(landing.Picks);
    }

    /// <summary>An arrow with no drawing on screen lands nowhere, leaves the keyboard off the map, and lets the key go on to the page.</summary>
    [Fact]
    public void AnArrowWithNoDrawingLandsNowhere()
    {
        var (keyboard, landings) = Keyboard(picked: 1);

        Assert.False(keyboard.Arrow(null, MapDirection.Right));
        Assert.Empty(landings);
        Assert.False(keyboard.IsActive);
    }

    /// <summary>The map taking the focus lands on what a click picked, without picking it again.</summary>
    [Fact]
    public void TakingTheFocusLandsOnWhatAClickPicked()
    {
        var (keyboard, landings) = Keyboard(picked: 3);

        keyboard.TakeFocus(Drawing);

        var landing = Assert.Single(landings);
        Assert.Equal(3, landing.Shape.Node);
        Assert.False(landing.Picks);
        Assert.True(keyboard.IsActive);
    }

    /// <summary>The menu key after a click opens on what the click picked, and picks it as a right-click does.</summary>
    [Fact]
    public void TheMenuKeyAfterAClickLandsOnWhatItPicked()
    {
        var (keyboard, landings) = Keyboard(picked: 2);

        Assert.Equal(2, keyboard.Menu(Drawing)?.Node);

        var landing = Assert.Single(landings);
        Assert.Equal(2, landing.Shape.Node);
        Assert.True(landing.Picks);
    }

    /// <summary>
    /// The menu key after a click on something the drawing on screen does not draw there opens no menu,
    /// and lands nowhere, rather than act on the shape nearest it (§7.1).
    /// </summary>
    [Fact]
    public void TheMenuKeyAfterAClickOnSomethingNotDrawnDoesNothing()
    {
        var (keyboard, landings) = Keyboard(picked: 4);

        Assert.Null(keyboard.Menu(Drawing));
        Assert.Empty(landings);
        Assert.False(keyboard.IsActive);
    }

    /// <summary>The pointer taking the map says whether the keyboard had it, so the map unmarks its shape only then.</summary>
    [Fact]
    public void LeavingSaysWhetherTheKeyboardHadTheMap()
    {
        var (keyboard, _) = Keyboard(picked: null);

        Assert.False(keyboard.Leave());

        keyboard.TakeFocus(Drawing);

        Assert.True(keyboard.Leave());
        Assert.False(keyboard.IsActive);
        Assert.Null(keyboard.On);
    }

    private static (ExploreKeyboard Keyboard, List<ExploreKeyboardLanding> Landings) Keyboard(int? picked)
    {
        var keyboard = new ExploreKeyboard(picked is { } node ? new HashSet<int> { node } : [], _ => null);
        var landings = new List<ExploreKeyboardLanding>();

        keyboard.Landed += (_, landing) => landings.Add(landing);

        return (keyboard, landings);
    }

    private static ExploreTree Files(int count)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, count).Select(i => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 1000))]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
