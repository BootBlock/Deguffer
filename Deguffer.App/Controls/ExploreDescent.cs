using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Controls;

/// <summary>
/// The clock behind a change of folder on the map: where the outer picture and the inner one are at
/// each frame of the flight between them, into the inner folder or out of it.
///
/// <para>The arithmetic is Core's: where the two pictures are is <see cref="MapDescent"/>'s, how the
/// flight moves is a <see cref="Spring"/>'s, and how long it takes is
/// <see cref="MotionToken.Entrance"/>'s. What is left here is following the frames.</para>
/// </summary>
internal sealed class ExploreDescent
{
    private readonly IMotionPolicy _motion;

    private readonly IFrameClock _clock;

    private MapDescent _path;

    /// <summary>How far the flight is from the outer picture, at 0, to the inner one, at 1.</summary>
    private Spring _progress = Spring.At(1);

    /// <summary>How the flight under way was asked to play, for the reader's setting at the time.</summary>
    private Motion _playing;

    public ExploreDescent(IMotionPolicy motion, IFrameClock clock)
    {
        _motion = motion;
        _clock = clock;
    }

    /// <summary>Raised at each frame of the flight, and once more at its end.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised once when the flight is over, whether it ran its course or was finished early.</summary>
    public event EventHandler? Arrived;

    public bool IsMoving { get; private set; }

    /// <summary>Where the inner picture's screen is on the screen now. See <see cref="MapDescent.Inner"/>.</summary>
    public MapFrame Inner { get; private set; } = MapFrame.Whole;

    /// <summary>Where the outer picture's screen is on the screen now. See <see cref="MapDescent.Outer"/>.</summary>
    public MapFrame Outer { get; private set; } = MapFrame.Whole;

    /// <summary>How opaque the inner picture is now.</summary>
    public double Opacity { get; private set; } = 1;

    /// <summary>
    /// Fly <paramref name="step"/> between an outer picture and an inner one whose screen lies at
    /// <paramref name="shape"/> on the outer one's, in fractions of it: from the outer picture filling
    /// the screen into the inner one, or the other way.
    /// </summary>
    public void Start(MapFrame shape, FolderStep step)
    {
        var now = _clock.Now;
        var motion = _motion.For(MotionToken.Entrance);
        var inward = step == FolderStep.Into;

        _path = new MapDescent(shape, motion.Travels);
        _playing = motion;
        _progress = Spring.At(inward ? 0 : 1).Toward(inward ? 1 : 0, now, motion);

        Go(now);
    }

    /// <summary>
    /// Go back the way the flight came, from where it is now and at the speed it has, for a reader
    /// who asked for the folder it left before it arrived. Nothing jumps: the spring carries its speed
    /// into the turn.
    /// </summary>
    public void Turn()
    {
        if (!IsMoving)
        {
            return;
        }

        var now = _clock.Now;

        _progress = _progress.Toward(1 - _progress.Target, now, _playing);

        Go(now);
    }

    /// <summary>
    /// End the flight where it was going. For anything that needs the screen settled first: another
    /// drawing, a wheel turn, a drag, or the map leaving the screen.
    /// </summary>
    public void Finish()
    {
        if (!IsMoving)
        {
            return;
        }

        _clock.Frame -= OnFrame;
        IsMoving = false;

        Follow(_progress.Target);
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    private void Go(TimeSpan now)
    {
        if (!IsMoving)
        {
            _clock.Frame += OnFrame;
            IsMoving = true;
        }

        if (_progress.IsOverAt(now))
        {
            Finish();
            return;
        }

        Follow(_progress.ValueAt(now));
    }

    /// <summary>
    /// One frame of the flight. Subscribed only while there is one, because the event fires at every
    /// frame the window composes.
    /// </summary>
    private void OnFrame(object? sender, object e)
    {
        var now = _clock.Now;

        // Animation effects turned off or on during the flight: it arrives rather than playing out a
        // move made under the setting the reader has just changed.
        if (_progress.IsOverAt(now) || _motion.For(MotionToken.Entrance) != _playing)
        {
            Finish();
            return;
        }

        Follow(_progress.ValueAt(now));
    }

    private void Follow(double progress)
    {
        Inner = _path.Inner(progress);
        Outer = _path.Outer(progress);
        Opacity = _path.Opacity(progress);

        Moved?.Invoke(this, EventArgs.Empty);
    }
}
