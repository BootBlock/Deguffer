using System.Diagnostics;
using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Controls;

/// <summary>
/// Where a map's zoom is, where it is going, and the clock that eases it from one to the other.
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about a viewport moving
/// over time, and it knows nothing of what is drawn in it (G1). The arithmetic is Core's —
/// <see cref="MapViewport"/> and <see cref="MapGlide"/> — so what is left here is the frame clock,
/// which needs a window.</para>
/// </summary>
internal sealed class ExploreZoom
{
    /// <summary>
    /// How far one notch of the wheel zooms: three notches double it. A notch is the unit a wheel
    /// reports in, and a precision touchpad reports fractions of one, which this scales by.
    /// </summary>
    private const double NotchesPerDoubling = 3;

    /// <summary>What a wheel reports for one notch (WHEEL_DELTA).</summary>
    private const double Notch = 120;

    /// <summary>One clock for the life of the map, read at each frame rather than started per move (G5).</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private MapGlide? _glide;

    /// <summary>
    /// Where the zoom is going. Each notch is measured from here rather than from what is on screen,
    /// so a quick run of them adds up to as many notches rather than to one.
    /// </summary>
    private MapViewport _target;

    /// <summary>
    /// Raised at each frame while the zoom is moving. A frame is the display's, so this is sixty or
    /// more times a second and must cost next to nothing to answer.
    /// </summary>
    public event EventHandler? Moved;

    /// <summary>Raised once when a move arrives, which is when the picture is worth drawing again.</summary>
    public event EventHandler? Arrived;

    /// <summary>The viewport on screen at this moment.</summary>
    public MapViewport Shown { get; private set; }

    /// <summary>
    /// Zoom by <paramref name="delta"/> of the wheel, in its own units, at the screen point
    /// (<paramref name="x"/>, <paramref name="y"/>) given as fractions of the screen.
    ///
    /// <para>What is under the pointer now is what stays under it, measured on the screen as it is
    /// rather than as it will be. Partway through a move the two differ, and anchoring to the target
    /// would pull the picture away from under a pointer that had not moved.</para>
    /// </summary>
    public void Turn(int delta, double x, double y)
    {
        var (pictureX, pictureY) = Shown.PictureAt(x, y);
        var zoom = _target.Zoom * Math.Pow(2, delta / Notch / NotchesPerDoubling);
        var target = MapViewport.Anchored(zoom, pictureX, pictureY, x, y);

        // Already going there: at either end of the zoom a further notch asks for nothing, and
        // restarting the move would ease again over a distance of nothing.
        if (target == _target)
        {
            return;
        }

        _target = target;

        if (_glide is null)
        {
            CompositionTarget.Rendering += OnRendering;
        }

        _glide = new MapGlide(Shown, target, _clock.Elapsed);
    }

    /// <summary>Back to the whole picture at once, for a map that has been handed something else to draw.</summary>
    public void Reset()
    {
        Stop();

        Shown = MapViewport.Whole;
        _target = MapViewport.Whole;
    }

    /// <summary>
    /// Finish any move where it was going, without raising anything. For a map leaving the screen:
    /// a frame clock left running would ease a picture nobody can see, and the map draws the arrival
    /// when it is back.
    /// </summary>
    public void Stop()
    {
        if (_glide is null)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _glide = null;
        Shown = _target;
    }

    /// <summary>
    /// One frame of the move. Subscribed only while there is a move, because the event fires at every
    /// frame the window composes, and an idle map would otherwise pay for it for as long as it is open.
    /// </summary>
    private void OnRendering(object? sender, object e)
    {
        if (_glide is not { } glide)
        {
            return;
        }

        var now = _clock.Elapsed;

        Shown = glide.At(now);

        if (!glide.IsOverAt(now))
        {
            Moved?.Invoke(this, EventArgs.Empty);
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _glide = null;

        Arrived?.Invoke(this, EventArgs.Empty);
    }
}
