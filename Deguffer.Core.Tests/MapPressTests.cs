using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a tap, a right-tap or a double-tap on the map may act. One that acts when it should not
/// picks or opens a shape the reader was not aiming at: one the step back is about to replace, or one a
/// change of view moved from under the pointer (§7.1).
/// </summary>
public sealed class MapPressTests
{
    [Fact]
    public void AMapNotYetPressedLetsEveryTapAct()
    {
        var press = new MapPress();

        Assert.True(press.TapPicks(dragged: false));
        Assert.True(press.RightTapActs);
        Assert.True(press.DoubleTapOpens(dragged: false));
    }

    /// <summary>The left button, a touch and a pen pick and open.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ALeftButtonTouchOrPenPressTaps(bool mouse, bool leftButton)
    {
        var press = new MapPress();

        press.Press(mouse, leftButton, landedMorph: false);

        Assert.True(press.TapPicks(dragged: false));
        Assert.True(press.RightTapActs);
        Assert.True(press.DoubleTapOpens(dragged: false));
    }

    /// <summary>
    /// The framework makes taps of the mouse's other buttons too. Back and Forward pick and open nothing,
    /// and the right button's own tap is the right-tap, which still acts.
    /// </summary>
    [Fact]
    public void AnotherMouseButtonNeitherPicksNorOpens()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: false, landedMorph: false);

        Assert.False(press.TapPicks(dragged: false));
        Assert.False(press.DoubleTapOpens(dragged: false));
        Assert.True(press.RightTapActs);
    }

    [Fact]
    public void APressThatDraggedNeitherPicksNorOpens()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: true, landedMorph: false);

        Assert.False(press.TapPicks(dragged: true));
        Assert.False(press.DoubleTapOpens(dragged: true));
    }

    [Fact]
    public void APressThatLandedAChangeOfViewTakesNoClick()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: true, landedMorph: true);

        Assert.False(press.TapPicks(dragged: false));
        Assert.False(press.RightTapActs);
        Assert.False(press.DoubleTapOpens(dragged: false));
    }

    /// <summary>
    /// A double-click is two presses, and the second finds the change already landed by the first: the
    /// second press's own tap still picks, but the pair opens nothing.
    /// </summary>
    [Fact]
    public void ADoubleTapWhoseFirstPressLandedAChangeOfViewOpensNothing()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: true, landedMorph: true);
        press.Press(mouse: true, leftButton: true, landedMorph: false);

        Assert.True(press.TapPicks(dragged: false));
        Assert.True(press.RightTapActs);
        Assert.False(press.DoubleTapOpens(dragged: false));
    }

    [Fact]
    public void AChangeOfViewTwoPressesBackNoLongerCounts()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: true, landedMorph: true);
        press.Press(mouse: true, leftButton: true, landedMorph: false);
        press.Press(mouse: true, leftButton: true, landedMorph: false);

        Assert.True(press.DoubleTapOpens(dragged: false));
    }

    /// <summary>A Back button press after a left one is judged by itself, not by the press before.</summary>
    [Fact]
    public void EachPressIsJudgedByItsOwnButton()
    {
        var press = new MapPress();

        press.Press(mouse: true, leftButton: true, landedMorph: false);
        press.Press(mouse: true, leftButton: false, landedMorph: false);

        Assert.False(press.TapPicks(dragged: false));

        press.Press(mouse: true, leftButton: true, landedMorph: false);

        Assert.True(press.TapPicks(dragged: false));
    }
}
