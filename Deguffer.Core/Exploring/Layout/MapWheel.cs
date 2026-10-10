namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where a turn of the mouse wheel takes the map's camera: a zoom at the pointer, or with Shift held,
/// or on a wheel that tilts, a pan across.
///
/// <para>Each notch is measured from where the camera is going rather than from what is on screen, so
/// a quick run of them adds up to as many notches rather than to one. What is under the pointer is
/// measured on the screen as it is, though, because partway through a move the two differ, and
/// anchoring to where the camera is going would pull the picture away from under a pointer that had
/// not moved.</para>
///
/// <para>Ctrl and the wheel together never come here: the compositor zooms the picture for them on
/// its own (see <see cref="MapTracking"/>). It cannot be asked to zoom for the wheel alone, which a
/// map needs, because there is no up and down in a map to scroll through.</para>
/// </summary>
public static class MapWheel
{
    /// <summary>What a wheel reports for one notch (WHEEL_DELTA). A precision wheel reports fractions of one.</summary>
    public const double Notch = 120;

    /// <summary>How many notches double the zoom.</summary>
    public const double NotchesPerDoubling = 3;

    /// <summary>
    /// The camera going to <paramref name="target"/>, showing <paramref name="shown"/>, after a turn of
    /// <paramref name="delta"/> at screen point (<paramref name="x"/>, <paramref name="y"/>), given as
    /// fractions of the screen. Away from the reader zooms in.
    /// </summary>
    public static MapViewport Zoom(MapViewport target, MapViewport shown, int delta, double x, double y)
    {
        var (pictureX, pictureY) = shown.PictureAt(x, y);

        return MapViewport.Anchored(target.Zoom * Math.Pow(2, delta / Notch / NotchesPerDoubling), pictureX, pictureY, x, y);
    }

    /// <summary>
    /// The camera going to <paramref name="target"/> after a turn of <paramref name="delta"/> across:
    /// each notch shows <see cref="MapKeys.PanStep"/> of the screen more to the left, for a turn away
    /// from the reader with Shift held or a wheel tilted left, as a scroll bar does.
    /// </summary>
    public static MapViewport Across(MapViewport target, int delta) =>
        target.Panned(delta / Notch * MapKeys.PanStep, 0);
}
