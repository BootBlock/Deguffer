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
/// believe (<see cref="MapRequests"/>), how a move is asked for (<see cref="MapTrackerMove"/>), how a
/// drag gives at an edge (<see cref="MapStretch"/>), what a hand letting go does (<see cref="MapFling"/>),
/// how far the zoom goes (<see cref="MapCeiling"/>) and what follows a move of the origin
/// (<see cref="MapOriginMove"/>). The requests are put to the tracker by <see cref="TrackerMoves"/>, and
/// the origin is moved by <see cref="ExploreOrigin"/>. What is left here is what the camera is doing
/// (<see cref="MapMotion"/>) and what the screen shows as the tracker answers (G1).</para>
///
/// <para>A reader who has turned animation effects off gets no inertia, no spring at an edge, and a
/// jump in place of every glide (<see cref="MotionToken.Camera"/>).</para>
///
/// <para>Over 500 lines because it is the one party that hears the tracker: every report is heard
/// here, so which report is believed and what the map takes the screen to show stay consistent with
/// each other.</para>
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

    private readonly ExploreOrigin _origin;

    /// <summary>What is moving the camera. Set through <see cref="Become"/>, which runs the jump's settle timer.</summary>
    private MapMotion _moving;

    /// <summary>The map's size, in device-independent pixels, which is what the tracker's pixels are.</summary>
    private (double Width, double Height) _size;

    /// <summary>What the tracker last reported, as it showed it, for where it comes to rest.</summary>
    private MapViewport _reported;

    /// <summary>What the tracker last reported, as it reported it, before anything held it to the ceiling.</summary>
    private MapTracking? _tracking;

    /// <summary>Whether the picture may stretch past its limits and coast, which is whether the reader has animation effects on.</summary>
    private bool _elastic;

    public ExploreZoom(Visual source, MapCamera camera, IMotionPolicy motion, DispatcherQueue dispatcher)
    {
        _motion = motion;
        _camera = camera;

        var compositor = source.Compositor;

        _tracker = InteractionTracker.CreateWithOwner(compositor, new WeakTrackerOwner(this));
        _tracker.MinScale = 1;
        _tracker.MaxScale = (float)Ceiling;
        camera.Bound(_tracker);

        _interaction = new MapInteraction(source, _tracker);
        _origin = new ExploreOrigin(_tracker, camera, _requests);
        _origin.Moved += (_, _) => OriginMoved?.Invoke(this, EventArgs.Empty);

        _settle = dispatcher.CreateTimer();
        _settle.Interval = JumpSettleTime;
        _settle.IsRepeating = false;
        _settle.TickGuarded(this, static zoom => zoom.Settled());

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
    public MapOrigin Origin => _origin.Origin;

    /// <summary>The viewport on screen at this moment.</summary>
    public MapViewport Shown { get; private set; }

    /// <summary>
    /// How far the picture can be magnified, which the drawing on screen decides (see
    /// <see cref="MapCeiling"/>). Every zoom the map asks for is held to it, and so are the tracker and
    /// the camera.
    /// </summary>
    public double Ceiling { get; private set; } = MapCeiling.Least;

    /// <summary>Where the camera is going, which is where it is when nothing is moving it.</summary>
    public MapViewport Target { get; private set; }

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
        _interaction.Coast(_elastic);
        _origin.Follow(_elastic);

        if (_elastic)
        {
            return;
        }

        var springing = _camera.Unstretch();

        if (_moving.By != MapMover.Tracker && !springing)
        {
            return;
        }

        Stop();

        Moved?.Invoke(this, EventArgs.Empty);
        Rest();
    }

    /// <summary>The drawing on screen allows a zoom up to <paramref name="ceiling"/>. See <see cref="MapCeiling.Drawn"/>.</summary>
    public void Limit(double ceiling) => Cap(MapCeiling.Drawn(ceiling, Ceiling, Target, Shown));

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

        // A drag measures the screen it started on, so it ends here, and the next move of the mouse
        // starts a new one at the new size.
        if (_moving.Drag is not null)
        {
            Become(MapMotion.Still);
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
    public void Turn(int delta, double x, double y) => GlideTo(MapWheel.Zoom(Target, Shown, delta, x, y, Ceiling));

    /// <summary>Pan across by <paramref name="delta"/> of the wheel. See <see cref="MapWheel.Across"/>.</summary>
    public void Slide(int delta) => GlideTo(MapWheel.Across(Target, delta));

    /// <summary>Move as <paramref name="key"/> asks. See <see cref="MapKeys"/>.</summary>
    public void Press(MapKey key) => GlideTo(MapKeys.Step(key, Target, Ceiling));

    /// <summary>
    /// Move from what is on screen to <paramref name="target"/>, or jump there for a reader who has
    /// turned animation effects off. A jump arrives once it has rested for <see cref="JumpSettleTime"/>,
    /// so a run of them is drawn once. A target deeper than the <see cref="Ceiling"/> raises it (see
    /// <see cref="MapCeiling.Toward"/>).
    /// </summary>
    public void GlideTo(MapViewport target) => MoveTo(target, _motion.For(MotionToken.Camera));

    /// <summary>
    /// Jump from what is on screen to <paramref name="target"/>, whatever the reader's motion setting,
    /// for a hand that is moving the camera from somewhere other than the picture: the screen goes where
    /// the hand puts it, as it does for a drag of the picture. It arrives as a jump does.
    /// </summary>
    public void JumpTo(MapViewport target) => MoveTo(target, Motion.Instant);

    /// <summary>
    /// To <paramref name="target"/> as <paramref name="motion"/> says, for a move timed with something
    /// else on the map, such as the camera following a shape that travels to a new layout. See
    /// <see cref="GlideTo"/>.
    /// </summary>
    public void MoveTo(MapViewport target, Motion motion)
    {
        // Already going there: at either end of the zoom a further notch asks for nothing, and
        // restarting the move would ease again over a distance of nothing.
        if (target == Target)
        {
            return;
        }

        Cap(MapCeiling.Toward(Ceiling, target));
        Target = target;
        Retargeted?.Invoke(this, EventArgs.Empty);

        Go(target, motion);
    }

    /// <summary>
    /// A hand pressed on the picture: a move on its way stops where it is, so what the press lands on
    /// is what the screen shows, and a drag carries on from there.
    /// </summary>
    public void Halt()
    {
        if (_moving.By == MapMover.Tracker)
        {
            _requests.Halted(TrackerMoves.Halt(_tracker));
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
        if (_moving.Drag is not { } drag)
        {
            drag = new MapStretch(MapTracking.Of(Shown, _size.Width, _size.Height, Origin), _size.Width, _size.Height, _elastic);
            Become(MapMotion.Dragged(drag));
        }

        _fling.Track(handX, handY, at);

        PullTo(drag.Move(x, y, Ceiling));
    }

    /// <summary>
    /// The mouse let go at <paramref name="at"/>. The picture carries on at the speed the hand had,
    /// and springs back inside the picture where the drag stretched it past an edge; for a reader who
    /// has turned animation effects off it stays where it was put. See <see cref="MapFling.LetGo"/>.
    /// </summary>
    public void Release(TimeSpan at)
    {
        if (_moving.Drag is null)
        {
            return;
        }

        var speed = _fling.Release(at);

        _fling.Clear();

        var held = Shown.Held(Ceiling);

        // The hand let go, so the drag is over without being cut short: whatever stretch it gave the
        // camera springs back below rather than being taken away (see MapMotion.CutsShort).
        _moving = MapMotion.Still;

        switch (MapFling.LetGo(held != Shown, _elastic, speed))
        {
            case MapLetGo.SpringBack:
                // The map takes it as where it is going from now, which is where a press during the
                // spring lands it.
                Shown = held;
                Target = held;
                _camera.SpringBack(_motion.For(MotionToken.Camera), Rest);
                break;

            case MapLetGo.Rest:
                Rest();
                break;

            case MapLetGo.Fling:
                Become(MapMotion.Tracked);
                _requests.Asked(TrackerMoves.Fling(_tracker, speed), null);
                break;
        }
    }

    /// <summary>Hand a touch on the map to the compositor. See <see cref="MapInteraction.Redirect"/>.</summary>
    public void Redirect(PointerPoint point) => _interaction.Redirect(point);

    /// <summary>
    /// To <paramref name="viewport"/> at once, for a map that has been handed something else to draw:
    /// the whole of it, or the part the reader had zoomed to when they were last there. The
    /// <see cref="Ceiling"/> starts again (see <see cref="MapCeiling.Afresh"/>).
    /// </summary>
    public void Reset(MapViewport viewport)
    {
        Become(MapMotion.Still);
        Cap(MapCeiling.Afresh(viewport));

        var moved = viewport != Target;

        Jump(viewport);
        Target = viewport;

        if (moved)
        {
            Retargeted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Finish any move where it was going, and a drag where it is held inside the picture, without
    /// raising anything. For a map leaving the screen, where a move left going would ease a picture
    /// nobody can see and the map draws the arrival when it is back, and for a map handed a new picture
    /// of the same place during a drag, which it draws where the drag stops.
    /// </summary>
    public void Stop()
    {
        var tracked = _moving.By == MapMover.Tracker;

        Become(MapMotion.Still);

        if (tracked)
        {
            Jump(Target);
        }
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
        if (_origin.Pending is { } move)
        {
            if (!move.CarriedOutBy(args.RequestId))
            {
                move.HeardRest(args.RequestId, _requests.Reports(0));
                return;
            }

            Carried(move);
        }

        if (!_requests.Reports(args.RequestId))
        {
            return;
        }

        _requests.Settled();

        if (_moving.By != MapMover.Tracker)
        {
            return;
        }

        Become(MapMotion.Still);
        Shown = _requests.Rest(_reported, Ceiling);
        Target = Shown;

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

        Become(MapMotion.Tracked);

        var position = args.ModifiedRestingPosition ?? args.NaturalRestingPosition;
        var scale = args.ModifiedRestingScale ?? args.NaturalRestingScale;

        Target = Read(new MapTracking(position.X, position.Y, scale, _origin.MeasuredFor(args.RequestId))).Held(Ceiling);
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.InteractingStateEntered(
        InteractionTracker sender,
        InteractionTrackerInteractingStateEnteredArgs args) => TakenByHand();

    void IInteractionTrackerOwner.RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {
        if (_origin.Pending is { } move && move.RefusedBy(args.RequestId))
        {
            Refused(move);
        }

        // Refused while a hand holds the camera: it never went where it was asked, so the screen still
        // shows where the tracker last said it was, not what the map took it to show when it asked.
        if (!_requests.Refused(args.RequestId))
        {
            return;
        }

        Shown = _reported;
        Target = _reported.Held(Ceiling);

        Moved?.Invoke(this, EventArgs.Empty);
        Retargeted?.Invoke(this, EventArgs.Empty);
    }

    void IInteractionTrackerOwner.ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        if (_origin.Pending is { } move && move.CarriedOutBy(args.RequestId))
        {
            Carried(move);
        }

        if (!_requests.Reports(args.RequestId) || !HasSize)
        {
            return;
        }

        _tracking = new MapTracking(args.Position.X, args.Position.Y, args.Scale, _origin.MeasuredFor(args.RequestId));
        // Kept even while a move of the origin holds the camera, for a move the tracker refuses.
        _reported = Read(_tracking.Value);

        ReportShown();
    }

    private bool HasSize => _size.Width > 0 && _size.Height > 0;

    /// <summary>
    /// Change what is moving the camera to <paramref name="motion"/>, which ends whatever moved it
    /// before. A drag cut short takes its stretch with it (<see cref="MapMotion.CutsShort"/>), and a
    /// jump rests once <see cref="JumpSettleTime"/> passes with nothing else moving it.
    /// </summary>
    private void Become(MapMotion motion)
    {
        // The screen no longer shows the picture past the edge, so neither does what the map takes it
        // to show. A drag does not change the zoom, so holding it changes only the position.
        if (_moving.CutsShort(motion))
        {
            _camera.Unstretch();
            Shown = Shown.Held(Ceiling);
        }

        _settle.Stop();
        _moving = motion;

        if (motion.Settles)
        {
            _settle.Start();
        }
    }

    /// <summary>A jump rested for <see cref="JumpSettleTime"/>.</summary>
    private void Settled()
    {
        _moving = MapMotion.Still;
        Rest();
    }

    /// <summary>What the screen shows with the tracker at <paramref name="tracking"/>.</summary>
    private MapViewport Read(MapTracking tracking) => tracking.Shown(_size.Width, _size.Height, _elastic, Ceiling);

    /// <summary>
    /// Take what the tracker last reported as what the screen shows, and say so. Not while a move of
    /// the origin holds the camera, which still shows what it did, nor while a mouse holds the picture:
    /// the tracker reports a drag past an edge as held at the edge while the camera shows it past, and a
    /// click is resolved against what the camera shows (§7.1).
    /// </summary>
    private void ReportShown()
    {
        if (_origin.Pending is not null || _moving.Drag is not null)
        {
            return;
        }

        Shown = _requests.Where(_reported);
        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ask the tracker for one step of a mouse dragging the picture, <paramref name="pull"/>: the drag
    /// says what the screen shows.
    /// </summary>
    private void PullTo(MapPull pull)
    {
        _requests.Asked(TrackerMoves.Place(_tracker, pull.Tracker), null);
        _camera.Stretch(pull.StretchX, pull.StretchY);

        Shown = pull.Shown;
        Target = pull.Target;

        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ask the tracker to <paramref name="target"/> as <paramref name="motion"/> says: a jump where it
    /// is instant, a glide otherwise. The one way a glide is asked for, so an instant motion never
    /// reaches one (see <see cref="TrackerMoves.Glide"/>).
    /// </summary>
    private void Go(MapViewport target, Motion motion)
    {
        // A map not yet laid out has nothing to glide across: it starts where it was asked to go, and
        // the tracker is put there once the map has a size (see Resize).
        if (motion.IsInstant || !HasSize)
        {
            Jump(target);
            Moved?.Invoke(this, EventArgs.Empty);
            Become(MapMotion.Jumped);
            return;
        }

        Become(MapMotion.Tracked);
        _requests.Asked(TrackerMoves.Glide(_tracker, Between(Shown, target), motion), target);
    }

    /// <summary>
    /// Put the camera on <paramref name="viewport"/> at once, and take it as what the screen shows.
    /// Where the tracker is comes only from its reports, so a jump it refuses leaves that as it was
    /// (see <see cref="IInteractionTrackerOwner.RequestIgnored"/>).
    /// </summary>
    private void Jump(MapViewport viewport)
    {
        _camera.Unstretch();

        if (HasSize)
        {
            _requests.Asked(TrackerMoves.Jump(_tracker, Between(Shown, viewport)), viewport);
        }

        Shown = viewport;
    }

    /// <summary>The move from <paramref name="from"/> to <paramref name="to"/> on this map, measured from its origin.</summary>
    private MapTrackerMove Between(MapViewport from, MapViewport to) =>
        MapTrackerMove.Between(from, to, _size.Width, _size.Height, Origin);

    /// <summary>
    /// Hold every zoom to <paramref name="ceiling"/> from now on: the map's own, the tracker's, and the
    /// camera's for a reader who has turned animation effects off. Set before anything is asked of the
    /// tracker at a zoom it allows, which it would otherwise hold to the old one.
    /// </summary>
    private void Cap(double ceiling)
    {
        if (ceiling == Ceiling)
        {
            return;
        }

        var before = Ceiling;

        Ceiling = ceiling;
        _tracker.MaxScale = (float)ceiling;
        _camera.Limit(ceiling);

        // A screen that moves with the ceiling moves what the map takes it to show, or a click is
        // resolved through the zoom the screen showed before (§7.1).
        if (HasSize && _tracking is { } tracking && tracking.MovesWithCeiling(before, ceiling, _elastic))
        {
            _reported = tracking.Shown(_size.Width, _size.Height, elastic: false, ceiling);
            ReportShown();
        }
    }

    /// <summary>The camera came to rest where the screen shows: measure from there if it is far from the origin, then say so.</summary>
    private void Rest()
    {
        Rebase();
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Measure from what the screen shows, where that is more than <see cref="MapOrigin.Reach"/> from
    /// the origin, without moving anything on it (see <see cref="ExploreOrigin.Move"/>).
    /// </summary>
    private void Rebase()
    {
        if (!HasSize || _moving.Drag is not null || _origin.Pending is not null || !Origin.Drifted(Shown, _size.Width, _size.Height))
        {
            return;
        }

        // At rest the tracker is where the screen is, whichever origin it turns out to be measured
        // from, which is where it stays if the move is refused before a hand reports anything.
        _reported = Shown;
        _origin.Move(Shown, _size.Width, _size.Height);
    }

    /// <summary>
    /// The tracker carried out <paramref name="move"/>: it is measured from the new origin, and the
    /// camera follows it again. What follows is <see cref="MapOriginMove.Carried"/>'s.
    /// </summary>
    private void Carried(MapOriginMove move)
    {
        _origin.Carried(_elastic);

        var carried = move.Carried(_requests.Reports(0));

        if (carried == MapOriginCarried.Unchanged)
        {
            return;
        }

        _requests.Asked(move.Request, move.Viewport);
        Shown = move.Viewport;
        Target = move.Viewport;

        Moved?.Invoke(this, EventArgs.Empty);
        Retargeted?.Invoke(this, EventArgs.Empty);

        if (carried == MapOriginCarried.Coasts)
        {
            Become(MapMotion.Tracked);
            return;
        }

        Become(MapMotion.Still);
        Rest();
    }

    /// <summary>
    /// The tracker refused <paramref name="move"/> because a hand holds the camera: everything goes
    /// back to the old origin, with the screen where the hand has the tracker, and whatever the map
    /// asked for since is asked again from there.
    /// </summary>
    private void Refused(MapOriginMove move)
    {
        _origin.Refused(move, _elastic);

        Shown = _reported;
        Moved?.Invoke(this, EventArgs.Empty);

        if (_requests.AskedSince(move.Request))
        {
            Reask();
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
        if (_moving.Drag is { } drag)
        {
            drag.Remeasure(Origin);
            PullTo(drag.Move(0, 0, Ceiling));
            return;
        }

        if (_requests.Destination is not { } destination)
        {
            return;
        }

        // Asked again as the reader's setting is now: a hand can have started the move with animation
        // effects off, and then the camera jumps.
        if (_moving.By == MapMover.Tracker)
        {
            Go(destination, _motion.For(MotionToken.Camera));
            return;
        }

        Jump(destination);
        Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A hand took the camera on the compositor: every request before it is over.</summary>
    private void TakenByHand()
    {
        Become(MapMotion.Tracked);
        _requests.Taken();

        Started?.Invoke(this, EventArgs.Empty);
    }
}
