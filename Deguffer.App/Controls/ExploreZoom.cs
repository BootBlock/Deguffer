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

    /// <summary>What the tracker and every placement are measured from: see <see cref="Origin"/>.</summary>
    private MapOrigin _origin;

    /// <summary>The camera's move to a new origin, until the tracker answers it.</summary>
    private MapOriginMove? _move;

    /// <summary>Whether a hand's coast came to rest while the tracker had not yet answered the move.</summary>
    private bool _restedBeforeAnswer;

    /// <summary>How far the picture can be magnified: see <see cref="Ceiling"/>.</summary>
    private double _ceiling = MapCeiling.Least;

    public ExploreZoom(Visual source, MapCamera camera, IMotionPolicy motion, DispatcherQueue dispatcher)
    {
        _motion = motion;
        _camera = camera;

        var compositor = source.Compositor;

        _tracker = InteractionTracker.CreateWithOwner(compositor, this);
        _tracker.MinScale = 1;
        _tracker.MaxScale = (float)_ceiling;
        camera.Bound(_tracker);

        _interaction = new MapInteraction(source, _tracker);

        _settle = dispatcher.CreateTimer();
        _settle.Interval = JumpSettleTime;
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Rest();

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

    /// <summary>
    /// Raised when the camera starts measuring from a new <see cref="Origin"/>, or goes back to the old
    /// one. Every drawing, outline and name has to be placed from it again before this returns: the
    /// camera has been held where it was, measured from the new origin, and the screen would show
    /// the two out of step otherwise.
    /// </summary>
    public event EventHandler? OriginMoved;

    /// <summary>
    /// The point of the picture the tracker's position, and every placement on the camera, is measured
    /// from (see <see cref="MapOrigin"/>). It moves to what the screen shows when the camera comes to
    /// rest far enough from it, so the single-precision compositor places everything to a small
    /// fraction of a pixel at any zoom.
    /// </summary>
    public MapOrigin Origin => _origin;

    /// <summary>The viewport on screen at this moment.</summary>
    public MapViewport Shown { get; private set; }

    /// <summary>
    /// How far the picture can be magnified, which the drawing on screen decides (see
    /// <see cref="MapCeiling"/>). Every zoom the map asks for is held to it, and so are the tracker and
    /// the camera.
    /// </summary>
    public double Ceiling => _ceiling;

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
        _interaction.Elastic = _elastic;

        // A camera held while its origin moves follows the tracker again, as it now asks, once the
        // move is resolved.
        if (_move is null)
        {
            _camera.Track(_tracker, _elastic);
        }

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
        Rest();
    }

    /// <summary>
    /// The drawing on screen allows a zoom up to <paramref name="ceiling"/>.
    ///
    /// <para>Never below where the camera is going, nor below what it shows within the ceiling it had:
    /// a drawing can arrive while the camera is on its way somewhere deeper than the drawing was made
    /// at, and a lower ceiling stops the next zoom, never one already made. The drawing made where the
    /// camera stops sets it again.</para>
    /// </summary>
    public void Limit(double ceiling) =>
        Cap(Math.Max(ceiling, Math.Max(_target.Zoom, Math.Min(Shown.Zoom, _ceiling))));

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
    public void Turn(int delta, double x, double y) => GlideTo(MapWheel.Zoom(_target, Shown, delta, x, y, _ceiling));

    /// <summary>Pan across by <paramref name="delta"/> of the wheel. See <see cref="MapWheel.Across"/>.</summary>
    public void Slide(int delta) => GlideTo(MapWheel.Across(_target, delta));

    /// <summary>Move as <paramref name="key"/> asks. See <see cref="MapKeys"/>.</summary>
    public void Press(MapKey key) => GlideTo(MapKeys.Step(key, _target, _ceiling));

    /// <summary>
    /// Move from what is on screen to <paramref name="target"/>, or jump there for a reader who has
    /// turned animation effects off. A jump arrives once it has rested for <see cref="JumpSettleTime"/>,
    /// so a run of them is drawn once.
    ///
    /// <para>A target deeper than the <see cref="Ceiling"/> raises it: every target the map works out
    /// itself is held to it, so this is a place the reader was, which had detail to show when they
    /// were there. The drawing made there says whether it still has.</para>
    /// </summary>
    public void GlideTo(MapViewport target)
    {
        // Already going there: at either end of the zoom a further notch asks for nothing, and
        // restarting the move would ease again over a distance of nothing.
        if (target == _target)
        {
            return;
        }

        Cap(Math.Max(_ceiling, target.Zoom));
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

        // The mouse holds the camera now, which supersedes any move whose rest was still to come.
        _settle.Stop();
        _moving = false;
        _stretch ??= new MapStretch(MapTracking.Of(Shown, _size.Width, _size.Height, _origin), _size.Width, _size.Height, _elastic);
        _fling.Track(handX, handY, at);

        var tracking = _stretch.Pull(x, y);
        var held = tracking.Held(_size.Width, _size.Height, _ceiling);

        _requests.Asked(_tracker.TryUpdatePosition(new Vector3((float)held.X, (float)held.Y, 0)), null);
        _camera.Stretch(held.X - tracking.X, held.Y - tracking.Y);

        Shown = tracking.Shown(_size.Width, _size.Height, _elastic, _ceiling);
        _target = Shown.Held(_ceiling);

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

        var held = Shown.Held(_ceiling);

        // Stretched past an edge: it springs back inside rather than coasting on. The map takes it as
        // where it is going from now, which is where a press during the spring lands it.
        if (held != Shown)
        {
            Shown = held;
            _target = held;
            _camera.SpringBack(_motion.For(MotionToken.Camera), Rest);
            return;
        }

        if (!_elastic || (x == 0 && y == 0))
        {
            Rest();
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
    ///
    /// <para>The <see cref="Ceiling"/> belonged to the drawing of what the map showed before, so it
    /// goes back to its least, or to the zoom the reader was at, until the new picture's first drawing
    /// sets it.</para>
    /// </summary>
    public void Reset(MapViewport viewport)
    {
        _settle.Stop();
        _stretch = null;
        _moving = false;

        Cap(Math.Max(MapCeiling.Least, viewport.Zoom));

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
        // A rest before the move is a hand's coast ending. The tracker carries the move out next: it
        // refuses a request only while a hand holds it.
        if (_move is { } move)
        {
            if (!move.CarriedOutBy(args.RequestId))
            {
                _restedBeforeAnswer |= args.RequestId == 0 && _requests.Reports(0);
                return;
            }

            Resolve(carriedOut: true);
        }

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
        Shown = _requests.Rest(_reported, _ceiling);
        _target = Shown;

        Rest();
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

        _target = new MapTracking(position.X, position.Y, scale, OriginFor(args.RequestId))
            .Shown(_size.Width, _size.Height, _elastic, _ceiling)
            .Held(_ceiling);
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.InteractingStateEntered(
        InteractionTracker sender,
        InteractionTrackerInteractingStateEnteredArgs args) => TakenByHand();

    void IInteractionTrackerOwner.RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {
        if (_move is { } move && move.RefusedBy(args.RequestId))
        {
            Resolve(carriedOut: false);
        }

        // Refused while a hand holds the camera: it never went where it was asked, so the screen still
        // shows where the tracker last said it was, not what the map took it to show when it asked.
        if (!_requests.Refused(args.RequestId))
        {
            return;
        }

        Shown = _reported;
        _target = _reported.Held(_ceiling);

        Moved?.Invoke(this, EventArgs.Empty);
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        if (_move is { } move && move.CarriedOutBy(args.RequestId))
        {
            Resolve(carriedOut: true);
        }

        if (!_requests.Reports(args.RequestId) || !HasSize)
        {
            return;
        }

        _reported = new MapTracking(args.Position.X, args.Position.Y, args.Scale, OriginFor(args.RequestId))
            .Shown(_size.Width, _size.Height, _elastic, _ceiling);

        // A hand's report from before the tracker answered a move of the origin: the camera is held,
        // so the screen still shows what it did. Where the tracker is goes on being kept, for a move
        // the tracker refuses.
        if (_move is not null)
        {
            return;
        }

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

    /// <summary>
    /// Hold every zoom to <paramref name="ceiling"/> from now on: the map's own, the tracker's, and the
    /// camera's for a reader who has turned animation effects off. Set before anything is asked of the
    /// tracker at a zoom it allows, which it would otherwise hold to the old one.
    /// </summary>
    private void Cap(double ceiling)
    {
        ceiling = Math.Min(ceiling, MapCeiling.Most);

        if (ceiling == _ceiling)
        {
            return;
        }

        _ceiling = ceiling;
        _tracker.MaxScale = (float)ceiling;
        _camera.Limit(ceiling);
    }

    /// <summary>The camera came to rest where the screen shows: measure from there if it is far from the origin, then say so.</summary>
    private void Rest()
    {
        Rebase();
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Measure from what the screen shows, where that is more than <see cref="MapOrigin.Reach"/> from
    /// the origin, without moving anything on it.
    ///
    /// <para>The camera and every placement change together, on the UI thread: the camera is held
    /// where it is, measured from the new origin, and the map places everything from it again. The
    /// tracker is then asked to the same place measured from the new origin, and the camera follows
    /// it again once it answers (<see cref="Resolve"/>). The two cannot change in the same frame,
    /// because the tracker answers on the compositor.</para>
    /// </summary>
    private void Rebase()
    {
        if (!HasSize || _stretch is not null || _move is not null || !_origin.Drifted(Shown, _size.Width, _size.Height))
        {
            return;
        }

        var from = _origin;

        // At rest the tracker is where the screen is, whichever origin it turns out to be measured
        // from, which is where it stays if the move is refused before a hand reports anything.
        _reported = Shown;
        _restedBeforeAnswer = false;
        _origin = MapOrigin.At(Shown);
        _camera.Hold(Shown.Camera(_size.Width, _size.Height, _origin));
        _camera.Rebase(_origin);
        OriginMoved?.Invoke(this, EventArgs.Empty);

        var tracking = MapTracking.Of(Shown, _size.Width, _size.Height, _origin);
        var request = _tracker.TryUpdatePosition(new Vector3((float)tracking.X, (float)tracking.Y, 0));

        _move = new MapOriginMove(request, from, _origin, Shown);
        _requests.Asked(request, Shown);
    }

    /// <summary>
    /// The tracker answered the move to a new origin: it is measured from it now, where
    /// <paramref name="carriedOut"/>, or it refused because a hand holds the camera, and everything goes
    /// back to the old origin, with the screen where the hand has the tracker. Either way the camera
    /// follows the tracker again.
    /// </summary>
    private void Resolve(bool carriedOut)
    {
        var move = _move!.Value;

        _move = null;

        if (!carriedOut)
        {
            _origin = move.From;
            _camera.Rebase(_origin);
        }

        _camera.Settle();
        _camera.Track(_tracker, _elastic);

        if (!carriedOut)
        {
            OriginMoved?.Invoke(this, EventArgs.Empty);

            Shown = _reported;
            Moved?.Invoke(this, EventArgs.Empty);

            if (_requests.AskedSince(move.Request))
            {
                Reask();
            }
        }
        else if (_requests.Reports(0))
        {
            // A wheel or a touchpad began to coast before the move, and the move stopped it where the
            // move went: the tracker is there. It comes to rest there, unless the coast had already
            // rested, and a tracker at rest does not report coming to rest again.
            _requests.Asked(move.Request, move.Viewport);
            Shown = move.Viewport;
            _target = move.Viewport;

            Moved?.Invoke(this, EventArgs.Empty);
            Retargeted?.Invoke(this, EventArgs.Empty);

            _moving = !_restedBeforeAnswer;

            if (_restedBeforeAnswer)
            {
                Rest();
            }
        }
    }

    /// <summary>
    /// Ask again for what was asked after a move of the origin the tracker then refused: it was
    /// measured from the new origin, and the tracker is measured from the old one. A drag goes on from
    /// where the hand has the picture, and a glide or a jump goes again to where it was going. A fling
    /// is a speed rather than a place, and needs nothing.
    /// </summary>
    private void Reask()
    {
        if (_stretch is not null)
        {
            _stretch.Remeasure(_origin);

            var tracking = _stretch.Pull(0, 0);
            var held = tracking.Held(_size.Width, _size.Height, _ceiling);

            _requests.Asked(_tracker.TryUpdatePosition(new Vector3((float)held.X, (float)held.Y, 0)), null);

            // While a mouse holds the picture the drag says what the screen shows, as in Drag.
            Shown = tracking.Shown(_size.Width, _size.Height, _elastic, _ceiling);
            _target = Shown.Held(_ceiling);
            Moved?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_requests.Destination is not { } destination)
        {
            return;
        }

        if (_moving)
        {
            _requests.Asked(Animate(destination, _motion.For(MotionToken.Camera)), destination);
            return;
        }

        Jump(destination);
        Moved?.Invoke(this, EventArgs.Empty);
    }
    /// <summary>The origin a report caused by request <paramref name="id"/> is measured from.</summary>
    private MapOrigin OriginFor(long id) => _move is { } move ? move.MeasuredFor(id) : _origin;

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
        var tracking = MapTracking.Of(target, _size.Width, _size.Height, _origin);
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

        var tracking = MapTracking.Of(viewport, _size.Width, _size.Height, _origin);
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
