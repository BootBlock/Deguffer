using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Controls;

/// <summary>
/// The clock behind a change of folder on the map: where the outer picture and the inner one are at
/// each frame of the flight between them, into the inner folder or out of it, or out of one folder
/// and on into another through the folder that holds both.
///
/// <para>The arithmetic is Core's: where the two pictures are is <see cref="MapDescent"/>'s, how the
/// flight moves is a <see cref="Spring"/>'s, and how long it takes is
/// <see cref="MotionToken.Entrance"/>'s, and which leg of a flight through a folder is under way is
/// <see cref="MapCourse"/>'s. What is left here is following the frames.</para>
/// </summary>
internal sealed class ExploreDescent
{
    private readonly IMotionPolicy _motion;

    private readonly IFrameClock _clock;

    /// <summary>The way the flight under way goes, and which part of it is under way. See <see cref="MapCourse"/>.</summary>
    private MapCourse _course;

    /// <summary>
    /// How far the flight is from the outer picture, at 0, to the inner one, at 1; or, through a
    /// folder, from the folder left, at 0, to the folder going to, at 1 (see <see cref="MapPassage"/>).
    /// </summary>
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

    /// <summary>
    /// Raised once in a flight through a folder, at the frame that reaches the middle picture, before
    /// that frame moves anything: from here the folder left has gone and the folder going to is on its
    /// way in, so its picture is wanted. Before <see cref="Arrived"/> for a flight finished early.
    /// </summary>
    public event EventHandler? Midway;

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

        _course = MapCourse.Descent(new MapDescent(shape, motion.Travels));
        _playing = motion;
        _progress = Spring.At(inward ? 0 : 1).Toward(inward ? 1 : 0, now, motion);

        Go(now);
    }

    /// <summary>
    /// Fly out of a folder whose screen lies at <paramref name="outOf"/> on the picture of the folder
    /// that holds it and another, and on into that other one, whose screen lies at
    /// <paramref name="into"/> on the same picture, as one flight (see <see cref="MapPassage"/>). A
    /// folder with no shape on that picture, null, fades in place on its leg rather than flies.
    /// </summary>
    public void Pass(MapFrame? outOf, MapFrame? into)
    {
        var now = _clock.Now;
        var motion = _motion.For(MotionToken.Entrance);

        _course = MapCourse.Through(new MapPassage(Leg(outOf, motion), Leg(into, motion)));
        _playing = motion;
        _progress = Spring.At(0).Toward(1, now, motion);

        Go(now);
    }

    private static MapDescent Leg(MapFrame? shape, Motion motion) =>
        new(shape ?? MapFrame.Whole, motion.Travels && shape is not null);

    /// <summary>
    /// Go back the way the flight came, from where it is now and at the speed it has, for a reader
    /// who asked for the folder it left before it arrived. Nothing jumps: the spring carries its speed
    /// into the turn. A flight through a folder turns between the ends of its leg under way: the folder
    /// it left and the middle picture, or, past the middle, the middle picture and the folder going to.
    /// </summary>
    public void Turn()
    {
        if (!IsMoving)
        {
            return;
        }

        var now = _clock.Now;
        (_course, var back) = _course.Turned(_progress.Target);

        _progress = _progress.Toward(back, now, _playing);

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
        (_course, var midway) = _course.Reached(progress);

        if (midway)
        {
            Midway?.Invoke(this, EventArgs.Empty);
        }

        var (path, along) = _course.At(progress);

        Inner = path.Inner(along);
        Outer = path.Outer(along);
        Opacity = path.Opacity(along);

        Moved?.Invoke(this, EventArgs.Empty);
    }
}
