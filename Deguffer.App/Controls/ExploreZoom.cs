using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.Interactions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;

namespace Deguffer.App.Controls;

/// <summary>
/// Where a map's camera is, where it is going, and what moves it: the compositor's interaction
/// tracker, which the camera follows (see <see cref="MapCamera"/>).
///
/// <para>The tracker takes the touchpad, touch, and the wheel with Ctrl held on the compositor
/// (<see cref="MapInteraction"/>), so a pinch, a two-finger pan, a flick and a turn of the wheel with
/// Ctrl move the picture with nothing on the UI thread, and carry on with inertia once let go. What
/// the compositor cannot take is asked of it from here: a plain turn of the wheel, which glides; a
/// mouse drag, which flings with the speed the hand had as it let go; the keyboard; and a glide to
/// somewhere the map chose, such as a shape double-clicked or the zoom Back returns to.</para>
///
/// <para><see cref="MapViewport"/> stays the one account of what the screen shows. The tracker reports
/// where it is as it moves, and each report is read back as a viewport (<see cref="MapTracking"/>), so a
/// click mid-move resolves against what the screen shows (§7.1). The rules are Core's: which report to
/// believe (<see cref="MapRequests"/>), the key frames of a glide (<see cref="MapGlide"/>), how a drag
/// gives at an edge (<see cref="MapStretch"/>) and how fast a hand let go (<see cref="MapFling"/>).
/// What is left here is asking the tracker and hearing its answers (G1).</para>
///
/// <para>A reader who has turned animation effects off gets no inertia, no spring at an edge, and a
/// jump in place of every glide (<see cref="MotionToken.Camera"/>).</para>
///
/// <para>Over 500 lines because it is the one party that talks to the tracker: every request is asked
/// here and every report heard here, so which report is believed and what the map takes the screen
/// to show stay consistent with each other.</para>
/// </summary>
internal sealed class ExploreZoom : IInteractionTrackerOwner
{
    /// <summary>
    /// How long a camera that jumped has to rest before it is drawn afresh where it landed. Drawing at
    /// every jump would rasterise for a camera superseded before the paint finished, and a key held
    /// down repeats many times a second.
    /// </summary>
    private static readonly TimeSpan JumpSettleTime = TimeSpan.FromMilliseconds(120);

    private readonly IMotionPolicy _motion;

    private readonly MapCamera _camera;

    private readonly InteractionTracker _tracker;

    private readonly MapInteraction _interaction;

    private readonly DispatcherQueueTimer _settle;

    private readonly MapRequests _requests = new();

    private readonly MapFling _fling = new();

    /// <summary>The drag a mouse is making, from its first move past the threshold until it lets go.</summary>
    private MapStretch? _stretch;

    /// <summary>The map's size, in device-independent pixels, which is what the tracker's pixels are.</summary>
    private (double Width, double Height) _size;

    /// <summary>What the tracker last reported, as it showed it, for where it comes to rest.</summary>
    private MapViewport _reported;

    /// <summary>Whether the picture may stretch past its limits and coast, which is whether the reader has animation effects on.</summary>
    private bool _elastic;

    /// <summary>Whether the tracker is moving the camera on its own: a glide, a fling or a hand.</summary>
    private bool _moving;

    /// <summary>Where the camera is going, which is where it is when nothing is moving it.</summary>
    private MapViewport _target;

    public ExploreZoom(Visual source, MapCamera camera, IMotionPolicy motion, DispatcherQueue dispatcher)
    {
        _motion = motion;
        _camera = camera;

        var compositor = source.Compositor;

        _tracker = InteractionTracker.CreateWithOwner(compositor, this);
        _tracker.MinScale = 1;
        _tracker.MaxScale = (float)MapViewport.MaximumZoom;
        _tracker.MinPosition = Vector3.Zero;

        // The picture is laid out over the screen at scale 1, so the screen can go as far as the
        // magnified picture's far edge, and no further.
        var farthest = compositor.CreateExpressionAnimation(
            "Vector3((tracker.Scale - 1) * camera.Size.X, (tracker.Scale - 1) * camera.Size.Y, 0)");
        farthest.SetReferenceParameter("tracker", _tracker);
        farthest.SetReferenceParameter("camera", camera.Properties);
        _tracker.StartAnimation(nameof(InteractionTracker.MaxPosition), farthest);

        _interaction = new MapInteraction(source, _tracker);

        _settle = dispatcher.CreateTimer();
        _settle.Interval = JumpSettleTime;
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Arrived?.Invoke(this, EventArgs.Empty);

        FollowMotion();
    }

    /// <summary>
    /// Raised at each report of the camera moving, and once at a jump. A report is the display's, so
    /// this is sixty or more times a second and must cost next to nothing to answer.
    /// </summary>
    public event EventHandler? Moved;

    /// <summary>Raised once when the camera comes to rest, which is when the picture is worth drawing again.</summary>
    public event EventHandler? Arrived;

    /// <summary>Raised when <see cref="Target"/> moves at once: a glide setting off, a fling, a reset.</summary>
    public event EventHandler? Retargeted;

    /// <summary>
    /// Raised as the camera starts to move by a hand on the compositor, which the map hears nothing
    /// of until it has started: a pinch, a two-finger pan, a touch, or the wheel with Ctrl held.
    /// </summary>
    public event EventHandler? Started;

    /// <summary>The viewport on screen at this moment.</summary>
    public MapViewport Shown { get; private set; }

    /// <summary>Where the camera is going, which is where it is when nothing is moving it.</summary>
    public MapViewport Target => _target;

    /// <summary>Whether the camera can be moved. See <see cref="MapInteraction.Movable"/>.</summary>
    public bool Movable
    {
        get => _interaction.Movable;
        set => _interaction.Movable = value;
    }

    /// <summary>
    /// Follow the reader's Animation effects setting, which says whether the picture coasts and
    /// stretches. Asked again whenever the setting changes.
    ///
    /// <para>A move on its way when the reader turns animation effects off lands where it was going
    /// at once, as every move does for them from then on, and is drawn there.</para>
    /// </summary>
    public void FollowMotion()
    {
        _elastic = _motion.For(MotionToken.Camera).Travels;
        _camera.Track(_tracker, _elastic);
        _interaction.Elastic = _elastic;

        if (_elastic)
        {
            return;
        }

        var springing = _camera.Unstretch();

        if (!_moving && !springing)
        {
            return;
        }

        Stop();

        Moved?.Invoke(this, EventArgs.Empty);
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The map is <paramref name="width"/> by <paramref name="height"/> now. The tracker counts in
    /// pixels, so the same part of the picture is a different position at a new size.
    /// </summary>
    public void Resize(double width, double height)
    {
        if (_size == (width, height))
        {
            return;
        }

        _size = (width, height);
        _camera.Resize(width, height);

        if (width > 0 && height > 0)
        {
            Jump(Shown);
        }
    }

    /// <summary>
    /// Zoom by <paramref name="delta"/> of the wheel, in its own units, at the screen point
    /// (<paramref name="x"/>, <paramref name="y"/>) given as fractions of the screen. See
    /// <see cref="MapWheel.Zoom"/>.
    /// </summary>
    public void Turn(int delta, double x, double y) => GlideTo(MapWheel.Zoom(_target, Shown, delta, x, y));

    /// <summary>Pan across by <paramref name="delta"/> of the wheel. See <see cref="MapWheel.Across"/>.</summary>
    public void Slide(int delta) => GlideTo(MapWheel.Across(_target, delta));

    /// <summary>Move as <paramref name="key"/> asks. See <see cref="MapKeys"/>.</summary>
    public void Press(MapKey key) => GlideTo(MapKeys.Step(key, _target));

    /// <summary>
    /// Move from what is on screen to <paramref name="target"/>, or jump there for a reader who has
    /// turned animation effects off. A jump arrives once it has rested for <see cref="JumpSettleTime"/>,
    /// so a run of them is drawn once.
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
        Retargeted?.Invoke(this, EventArgs.Empty);

        var motion = _motion.For(MotionToken.Camera);

        // A map not yet laid out has nothing to glide across: it starts where it was asked to go, and
        // the tracker is put there once the map has a size (see Resize).
        if (motion.IsInstant || !HasSize)
        {
            Jump(target);
            Moved?.Invoke(this, EventArgs.Empty);

            _settle.Stop();
            _settle.Start();
            return;
        }

        _settle.Stop();
        _moving = true;
        _requests.Asked(Animate(target, motion), target);
    }

    /// <summary>
    /// A hand pressed on the picture: a move on its way stops where it is, so what the press lands on
    /// is what the screen shows, and a drag carries on from there.
    /// </summary>
    public void Halt()
    {
        if (_moving)
        {
            _requests.Halted(_tracker.TryUpdatePositionBy(Vector3.Zero));
        }

        _fling.Clear();

        // A spring back cut short lands where it was going, which is what the map already takes as
        // shown, and is drawn there.
        if (_camera.Unstretch())
        {
            Arrived?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Move the picture with a mouse dragging it by (<paramref name="x"/>, <paramref name="y"/>)
    /// device-independent pixels, with the pointer at (<paramref name="handX"/>,
    /// <paramref name="handY"/>) at <paramref name="at"/>. The picture goes where the hand goes, and
    /// past an edge only as far as <see cref="MapStretch"/> gives.
    ///
    /// <para><see cref="Moved"/> is raised at once, and <see cref="Arrived"/> waits for
    /// <see cref="Release"/>, because a drawing made at every step of a drag would be superseded
    /// before it was finished.</para>
    /// </summary>
    public void Drag(double x, double y, double handX, double handY, TimeSpan at)
    {
        if (!HasSize)
        {
            return;
        }

        _settle.Stop();
        _stretch ??= new MapStretch(MapTracking.Of(Shown, _size.Width, _size.Height), _size.Width, _size.Height, _elastic);
        _fling.Track(handX, handY, at);

        var tracking = _stretch.Pull(x, y);
        var held = tracking.Held(_size.Width, _size.Height);

        _requests.Asked(_tracker.TryUpdatePosition(new Vector3((float)held.X, (float)held.Y, 0)), null);
        _camera.Stretch(held.X - tracking.X, held.Y - tracking.Y);

        Shown = tracking.Shown(_size.Width, _size.Height, _elastic);
        _target = Shown.Held();

        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The mouse let go at <paramref name="at"/>. The picture carries on at the speed the hand had,
    /// and springs back inside the picture where the drag stretched it past an edge; for a reader who
    /// has turned animation effects off it stays where it was put.
    /// </summary>
    public void Release(TimeSpan at)
    {
        if (_stretch is null)
        {
            return;
        }

        _stretch = null;

        var (x, y) = _fling.Release(at);

        _fling.Clear();

        var held = Shown.Held();

        // Stretched past an edge: it springs back inside rather than coasting on. The map takes it as
        // where it is going from now, which is where a press during the spring lands it.
        if (held != Shown)
        {
            Shown = held;
            _target = held;
            _camera.SpringBack(_motion.For(MotionToken.Camera), () => Arrived?.Invoke(this, EventArgs.Empty));
            return;
        }

        if (!_elastic || (x == 0 && y == 0))
        {
            Arrived?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The picture moves with the hand, so the screen moves the other way.
        _moving = true;
        _requests.Asked(_tracker.TryUpdatePositionWithAdditionalVelocity(new Vector3((float)-x, (float)-y, 0)), null);
    }

    /// <summary>Hand a touch on the map to the compositor. See <see cref="MapInteraction.Redirect"/>.</summary>
    public void Redirect(PointerPoint point) => _interaction.Redirect(point);

    /// <summary>
    /// To <paramref name="viewport"/> at once, for a map that has been handed something else to draw:
    /// the whole of it, or the part the reader had zoomed to when they were last there.
    /// </summary>
    public void Reset(MapViewport viewport)
    {
        _settle.Stop();
        _stretch = null;
        _moving = false;

        var moved = viewport != _target;

        Jump(viewport);
        _target = viewport;

        if (moved)
        {
            Retargeted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Finish any move where it was going, without raising anything. For a map leaving the screen: a
    /// move left going would ease a picture nobody can see, and the map draws the arrival when it is
    /// back.
    /// </summary>
    public void Stop()
    {
        _settle.Stop();
        _stretch = null;

        if (!_moving)
        {
            return;
        }

        _moving = false;
        Jump(_target);
    }

    void IInteractionTrackerOwner.CustomAnimationStateEntered(
        InteractionTracker sender,
        InteractionTrackerCustomAnimationStateEnteredArgs args)
    {
    }

    void IInteractionTrackerOwner.IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {
        if (!_requests.Reports(args.RequestId))
        {
            return;
        }

        _requests.Settled();

        if (!_moving)
        {
            return;
        }

        _moving = false;
        Shown = _requests.Rest(_reported);
        _target = Shown;

        Arrived?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
    {
        // The wheel and the touchpad coast without the hand having been reported as holding anything.
        if (args.RequestId == 0)
        {
            TakenByHand();
        }

        if (!_requests.Reports(args.RequestId) || !HasSize)
        {
            return;
        }

        _moving = true;

        var position = args.ModifiedRestingPosition ?? args.NaturalRestingPosition;
        var scale = args.ModifiedRestingScale ?? args.NaturalRestingScale;

        _target = new MapTracking(position.X, position.Y, scale).Shown(_size.Width, _size.Height, _elastic).Held();
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.InteractingStateEntered(
        InteractionTracker sender,
        InteractionTrackerInteractingStateEnteredArgs args) => TakenByHand();

    void IInteractionTrackerOwner.RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {
        // Refused while a hand holds the camera: it never went where it was asked, so the screen still
        // shows where the tracker last said it was, not what the map took it to show when it asked.
        if (!_requests.Refused(args.RequestId))
        {
            return;
        }

        Shown = _reported;
        _target = _reported.Held();

        Moved?.Invoke(this, EventArgs.Empty);
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        if (!_requests.Reports(args.RequestId) || !HasSize)
        {
            return;
        }

        _reported = new MapTracking(args.Position.X, args.Position.Y, args.Scale)
            .Shown(_size.Width, _size.Height, _elastic);

        // While a mouse holds the picture the drag says what the screen shows. The tracker reports a
        // drag past an edge as held at the edge while the camera shows it past, and a click is
        // resolved against what the camera shows (§7.1).
        if (_stretch is not null)
        {
            return;
        }

        Shown = _requests.Where(_reported);

        Moved?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSize => _size.Width > 0 && _size.Height > 0;

    /// <summary>A hand took the camera on the compositor: every request before it is over.</summary>
    private void TakenByHand()
    {
        _settle.Stop();
        _requests.Taken();
        _moving = true;

        Started?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ask the tracker to play the move from what is on screen to <paramref name="target"/> as
    /// <paramref name="motion"/> says, and say which request it is.
    /// </summary>
    private int Animate(MapViewport target, Motion motion)
    {
        var compositor = _tracker.Compositor;

        if (MapTracking.Pivot(Shown, target, _size.Width, _size.Height) is { } pivot)
        {
            var linear = compositor.CreateLinearEasingFunction();
            var zoom = compositor.CreateScalarKeyFrameAnimation();

            // From wherever the tracker is when the move starts, which a report on its way may not
            // have told the map yet.
            zoom.InsertExpressionKeyFrame(0, "this.StartingValue");

            foreach (var (time, value) in new MapGlide(Shown, target).Zooms().Skip(1))
            {
                zoom.InsertKeyFrame((float)time, (float)value, linear);
            }

            zoom.Duration = motion.Duration;

            return _tracker.TryUpdateScaleWithAnimation(zoom, new Vector3((float)pivot.X, (float)pivot.Y, 0));
        }

        var (first, second) = Motion.EaseControlPoints;
        var tracking = MapTracking.Of(target, _size.Width, _size.Height);
        var pan = compositor.CreateVector3KeyFrameAnimation();

        pan.InsertKeyFrame(
            1,
            new Vector3((float)tracking.X, (float)tracking.Y, 0),
            compositor.CreateCubicBezierEasingFunction(first, second));
        pan.Duration = motion.Duration;

        return _tracker.TryUpdatePositionWithAnimation(pan);
    }

    /// <summary>
    /// Put the camera on <paramref name="viewport"/> at once, and take it as what the screen shows.
    /// Where the tracker is comes only from its reports, so a jump it refuses leaves that as it was
    /// (see <see cref="IInteractionTrackerOwner.RequestIgnored"/>).
    ///
    /// <para>One request where it can be: a scale about the move's still point carries the position
    /// with it. A scale and a position asked for one after the other in the same frame can leave the
    /// position wrong, which is a known fault of the tracker, so the two are only asked for together
    /// where the zoom barely changes and there is no still point within reach.</para>
    /// </summary>
    private void Jump(MapViewport viewport)
    {
        _camera.Unstretch();

        if (!HasSize)
        {
            Shown = viewport;
            return;
        }

        var tracking = MapTracking.Of(viewport, _size.Width, _size.Height);
        var position = new Vector3((float)tracking.X, (float)tracking.Y, 0);
        int id;

        if (MapTracking.Pivot(Shown, viewport, _size.Width, _size.Height) is { } pivot)
        {
            id = _tracker.TryUpdateScale((float)viewport.Zoom, new Vector3((float)pivot.X, (float)pivot.Y, 0));
        }
        else if (Shown.Zoom == viewport.Zoom)
        {
            id = _tracker.TryUpdatePosition(position);
        }
        else
        {
            _tracker.TryUpdateScale((float)viewport.Zoom, Vector3.Zero);
            id = _tracker.TryUpdatePosition(position);
        }

        _requests.Asked(id, viewport);

        Shown = viewport;
    }
}
