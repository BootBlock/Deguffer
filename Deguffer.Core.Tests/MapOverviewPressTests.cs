using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A press on the map's overview: a click flies the camera to where it was made, a drag of the
/// rectangle round the screen moves the screen with it, and a drag anywhere else keeps the middle of
/// the screen under the hand.
/// </summary>
public sealed class MapOverviewPressTests
{
    private const int Ceiling = 64;

    /// <summary>An overview 200 by 100, with the screen zoomed four times at a quarter across and halfway down.</summary>
    private const double Width = 200;

    private const double Height = 100;

    /// <summary>The rectangle round the screen covers 50 to 100 across and 50 to 75 down.</summary>
    private static readonly MapViewport Zoomed = MapViewport.Anchored(4, 0.25, 0.5, 0, 0, Ceiling);

    [Fact]
    public void AClickOffTheRectangleFliesTheCameraThere()
    {
        var press = Pressed(150, 25);

        Assert.Equal(MapOverview.Centred(Zoomed, 0.75, 0.25, Ceiling), press.Release());
        Assert.False(press.IsHeld);
    }

    [Fact]
    public void AClickOnTheRectangleGoesNowhere()
    {
        var press = Pressed(75, 60);

        Assert.Null(press.Release());
    }

    /// <summary>A hand that wanders under the threshold still clicks, at where it went down.</summary>
    [Fact]
    public void APressThatWandersAPixelIsStillAClick()
    {
        var press = Pressed(150, 25);

        Assert.Null(press.Move(150 + MapDrag.Threshold - 1, 26, held: true));
        Assert.Equal(MapOverview.Centred(Zoomed, 0.75, 0.25, Ceiling), press.Release());
    }

    /// <summary>
    /// The screen goes as far as the rectangle has gone from where it was pressed, so a drag adds
    /// up to where the hand is, and letting go of it flies nowhere.
    /// </summary>
    [Fact]
    public void DraggingTheRectangleMovesTheScreenWithIt()
    {
        var press = Pressed(75, 60);

        Assert.Equal(MapOverview.Dragged(Zoomed, 10 / Width, 0), press.Move(85, 60, held: true));
        Assert.Equal(MapOverview.Dragged(Zoomed, 20 / Width, 10 / Height), press.Move(95, 70, held: true));
        Assert.Null(press.Release());
    }

    [Fact]
    public void DraggingOffTheRectangleKeepsTheMiddleOfTheScreenUnderTheHand()
    {
        var press = Pressed(150, 25);

        Assert.Equal(MapOverview.Centred(Zoomed, 0.8, 0.25, Ceiling), press.Move(160, 25, held: true));
        Assert.Null(press.Release());
    }

    /// <summary>Let go somewhere the overview never heard about: the press is over, and flies nowhere.</summary>
    [Fact]
    public void AMoveWithTheButtonUpEndsThePress()
    {
        var press = Pressed(150, 25);

        Assert.Null(press.Move(160, 25, held: false));
        Assert.False(press.IsHeld);
        Assert.Null(press.Release());
    }

    private static MapOverviewPress Pressed(double x, double y)
    {
        var press = new MapOverviewPress();

        press.Press(x, y, Width, Height, Zoomed, Ceiling);

        Assert.True(press.IsHeld);

        return press;
    }
}
