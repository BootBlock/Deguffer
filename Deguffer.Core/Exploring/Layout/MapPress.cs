namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Whether a tap, a right-tap or a double-tap on the map may act, from the press that made it and the
/// press before.
///
/// <para>A tap picks, and a double-tap opens, only from a press of the left button, a touch or a pen. The
/// right button's own tap is the right-tap, which may pick and offer a menu. The framework makes a tap
/// and a double-tap of the mouse's Back and Forward buttons too, which pick and open nothing anywhere
/// else in Windows. Where the page goes back or forward with them, picking or opening the shape under
/// the pointer as well would act on a picture the step is about to replace.</para>
///
/// <para>A press that landed a change of view on its way takes no click: the shapes were where neither
/// view puts them, so whatever the press was aimed at is not what is under it now (§7.1). A double-tap
/// is two presses, and the second finds the change already landed by the first, so it asks about both.</para>
///
/// <para>In Core rather than in the shell, as <see cref="MapDrag"/> is, so the rules are provable
/// without a window (G8). Whether the press became a drag is <see cref="MapDrag.Dragged"/>'s to say.</para>
/// </summary>
public sealed class MapPress
{
    /// <summary>Whether the last press was one a tap may act on. Until a press says otherwise, a tap may act.</summary>
    private bool _tap = true;

    /// <summary>Whether the last press landed a change of view on its way.</summary>
    private bool _landedMorph;

    /// <summary>Whether the press before the last one landed a change of view.</summary>
    private bool _earlierLandedMorph;

    /// <summary>
    /// A press, by a <paramref name="mouse"/> or not, with its <paramref name="leftButton"/> down or not,
    /// that <paramref name="landedMorph"/> where a change of view was still moving when it went down.
    /// </summary>
    public void Press(bool mouse, bool leftButton, bool landedMorph)
    {
        _tap = !mouse || leftButton;
        _earlierLandedMorph = _landedMorph;
        _landedMorph = landedMorph;
    }

    /// <summary>Whether a tap from the last press picks the shape under it, where the press <paramref name="dragged"/> the picture or not.</summary>
    public bool TapPicks(bool dragged) => _tap && !_landedMorph && !dragged;

    /// <summary>Whether a right-tap from the last press may pick and offer a menu.</summary>
    public bool RightTapActs => !_landedMorph;

    /// <summary>Whether a double-tap ending in the last press opens the shape under it, where the press <paramref name="dragged"/> the picture or not.</summary>
    public bool DoubleTapOpens(bool dragged) => _tap && !_landedMorph && !_earlierLandedMorph && !dragged;
}
