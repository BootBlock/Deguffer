using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Controls;

/// <summary>
/// Where a map's zoom is, where it is going, and the clock that moves it from one to the other, or
/// the hand that drags it there.
///
/// <para>Separate from <see cref="ExploreMap"/> for the reason <see cref="ExploreHighlight"/> is.
/// That one is about which tree is drawn and what the pointer found; this is about a viewport moving
/// over time, and it knows nothing of what is drawn in it (G1). The arithmetic is Core's —
/// <see cref="MapViewport"/> and <see cref="MapGlide"/> — and how it moves is
/// <see cref="MotionToken.Camera"/>'s, so what is left here is following the frames.</para>
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

    /// <summary>
    /// How long a zoom that jumped has to rest before it is drawn afresh where it landed. The
    /// jump is on screen at once, over the drawings kept, as each frame of a glide is. Drawing at every
    /// notch would rasterise for a zoom superseded before the paint finished, and a precision touchpad
    /// reports many notches a second, so the map would fall behind the hand.
    /// </summary>
    internal static readonly TimeSpan JumpSettleTime = TimeSpan.FromMilliseconds(120);

    private readonly IMotionPolicy _motion;

    private readonly IFrameClock _clock;

    private MapGlide? _glide;

    /// <summary>
    /// Where the zoom is going. Each notch is measured from here rather than from what is on screen,
    /// so a quick run of them adds up to as many notches rather than to one.
    /// </summary>
    private MapViewport _target;

    /// <summary>
    /// Raised at each frame while the zoom is moving, and once at a jump. A frame is the display's, so
    /// this is sixty or more times a second and must cost next to nothing to answer.
    /// </summary>
    public event EventHandler? Moved;

    /// <summary>Raised once when a move arrives, which is when the picture is worth drawing again.</summary>
    public event EventHandler? Arrived;

    public ExploreZoom(IMotionPolicy motion, IFrameClock clock)
    {
        _motion = motion;
        _clock = clock;
    }

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

        GlideTo(MapViewport.Anchored(zoom, pictureX, pictureY, x, y));
    }

    /// <summary>
    /// Move from what is on screen to <paramref name="target"/>, or jump there for a reader who has
    /// turned animation effects off. A jump arrives once the wheel has rested for
    /// <see cref="JumpSettleTime"/>, so a run of notches is drawn once, as a glide's is.
    /// </summary>
    public void GlideTo(MapViewport target)
    {
        // Already going there: at either end of the zoom a further notch asks for nothing, and
        // restarting the move would ease again over a distance of nothing.
        if (target == _target)
        {
            return;
        }

        _target = target;

        var motion = _motion.For(MotionToken.Camera);

        if (_glide is null)
        {
            _clock.Frame += OnFrame;
        }

        _glide = new MapGlide(Shown, target, _clock.Now, motion);

        if (motion.IsInstant)
        {
            Shown = target;
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Move the picture with a hand dragging it by (<paramref name="x"/>, <paramref name="y"/>) of the
    /// screen, at once rather than eased: the picture is held, and it goes where the hand goes.
    ///
    /// <para>A move on its way stops where it is first, so the drag carries on from what is on screen.
    /// <see cref="Moved"/> is raised where the picture moved, and <see cref="Arrived"/> waits for
    /// <see cref="Release"/>, because a drawing made at every step of a drag would be superseded
    /// before it was finished.</para>
    /// </summary>
    public void Drag(double x, double y)
    {
        Halt();

        var panned = Shown.Panned(x, y);

        if (panned == Shown)
        {
            return;
        }

        Shown = panned;
        _target = panned;

        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The hand let go: the picture is where it will stay, and worth drawing there.</summary>
    public void Release() => Arrived?.Invoke(this, EventArgs.Empty);

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

        _clock.Frame -= OnFrame;
        _glide = null;
        Shown = _target;
    }

    /// <summary>Stop any move where it is on screen now, rather than where it was going.</summary>
    private void Halt()
    {
        if (_glide is null)
        {
            return;
        }

        _clock.Frame -= OnFrame;
        _glide = null;
        _target = Shown;
    }

    /// <summary>End the move where it was going, and say it has arrived.</summary>
    private void Land()
    {
        _clock.Frame -= OnFrame;
        _glide = null;
        Shown = _target;

        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One frame of the move. Subscribed only while there is a move, because the event fires at every
    /// frame the window composes, and an idle map would otherwise pay for it for as long as it is open.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        if (_glide is not { } glide)
        {
            return;
        }

        // Animation effects turned off or on while the picture was moving: it lands where it was going
        // rather than playing out a move made under the setting the reader has just changed.
        if (_motion.For(MotionToken.Camera) != glide.Motion)
        {
            Land();
            return;
        }

        var now = _clock.Now;

        // A jump is already where it was going, and waits only for the wheel to rest.
        if (glide.Motion.IsInstant)
        {
            if (now - glide.Start >= JumpSettleTime)
            {
                Land();
            }

            return;
        }

        Shown = glide.At(now);

        if (!glide.IsOverAt(now))
        {
            Moved?.Invoke(this, EventArgs.Empty);
            return;
        }

        Land();
    }
}
