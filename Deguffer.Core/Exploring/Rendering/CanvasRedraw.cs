using System.Collections.Concurrent;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// One drawing of a canvas on its way to the screen: laid out and painted on worker threads, and put
/// on screen a region at a time on the thread that asked for it. Made by <see cref="CanvasRedraws"/>.
///
/// <para>Every property here is read and changed only on the owner's thread, the one that started
/// the redraw. So what it says about the screen is what the screen shows: a region is
/// <see cref="Covers">covered</see> from the moment it is handed to the target, and not before,
/// however long ago a worker finished painting it.</para>
/// </summary>
public sealed class CanvasRedraw
{
    private readonly SynchronizationContext _owner;
    private readonly Func<ExploreSurface> _layout;
    private readonly TileColour _ground;
    private readonly ExplorePoint? _focus;
    private readonly ICanvasRedrawTarget _target;

    /// <summary>
    /// Not disposed. It has no timer, so it holds nothing beyond its own memory, and a worker can
    /// still be asking it when the redraw is superseded.
    /// </summary>
    private readonly CancellationTokenSource _cancel = new();

    /// <summary>Painted by a worker and not yet handed to the owner's thread.</summary>
    private readonly ConcurrentQueue<CanvasRegion> _painted = new();

    private readonly List<CanvasRegion> _landed = [];

    /// <summary>One while a hand-over is posted to the owner's thread and has not run, zero otherwise.</summary>
    private int _handOverPosted;

    internal CanvasRedraw(
        SynchronizationContext owner,
        Func<ExploreSurface> layout,
        byte[] pixels,
        TileColour ground,
        ExplorePoint? focus,
        ICanvasRedrawTarget target)
    {
        _owner = owner;
        _layout = layout;
        _ground = ground;
        _focus = focus;
        _target = target;
        Pixels = pixels;
    }

    /// <summary>
    /// The buffer the regions are painted into, at the drawing's size. A region's pixels in it are
    /// final once the region is handed to <see cref="ICanvasRedrawTarget.Land"/>.
    /// </summary>
    public byte[] Pixels { get; }

    /// <summary>The drawing, from <see cref="ICanvasRedrawTarget.Begin"/> on, and null before it.</summary>
    public ExploreSurface? Surface { get; private set; }

    /// <summary>Whether every region is on screen and the drawing is the picture.</summary>
    public bool IsArrived { get; private set; }

    /// <summary>Whether a newer redraw, or the owner, stopped this one before it arrived.</summary>
    public bool IsSuperseded { get; private set; }

    /// <summary>Whether this redraw has finished, one way or the other.</summary>
    public bool IsSettled => IsArrived || IsSuperseded;

    /// <summary>
    /// The regions on screen, while the redraw is landing. Empty before the first lands, and again
    /// once the redraw has arrived or been superseded.
    /// </summary>
    public IReadOnlyList<CanvasRegion> Landed => _landed;

    /// <summary>The work on the worker threads, which ends once nothing more of it will touch <see cref="Pixels"/>.</summary>
    internal Task Finished { get; set; } = Task.CompletedTask;

    /// <summary>
    /// Whether the canvas point <paramref name="x"/>, <paramref name="y"/> of this drawing is on
    /// screen while the redraw lands. False everywhere once it has arrived or been superseded: an
    /// arrived drawing is the whole picture, and a superseded one is off the screen.
    /// </summary>
    public bool Covers(float x, float y)
    {
        foreach (var region in _landed)
        {
            if (region.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stop this redraw, on the owner's thread. Nothing more of it reaches the target, and what of it
    /// had landed is withdrawn.
    /// </summary>
    internal void Supersede()
    {
        if (IsSettled)
        {
            return;
        }

        IsSuperseded = true;
        _cancel.Cancel();

        if (Surface is not null)
        {
            _landed.Clear();
            _target.Withdraw(this);
        }
    }

    /// <summary>
    /// Lay the drawing out and paint it, on a worker thread, with at most <paramref name="workers"/>
    /// threads painting at once.
    ///
    /// <para>The regions are handed to the painting threads one at a time and in
    /// <see cref="PaintOrder"/>, so the first to finish are the ones the reader is looking at. A
    /// partitioner that handed each thread a run of them would paint the far corner of the canvas as
    /// early as its middle.</para>
    /// </summary>
    internal void Run(int workers)
    {
        var token = _cancel.Token;

        try
        {
            token.ThrowIfCancellationRequested();

            var surface = _layout();

            token.ThrowIfCancellationRequested();

            var painter = surface.Painter(_ground);
            var regions = PaintOrder.Regions(surface.Width, surface.Height, _focus);

            _owner.Post(_ => Begin(surface), null);

            Parallel.ForEach(
                Partitioner.Create(regions, EnumerablePartitionerOptions.NoBuffering),
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = token },
                region =>
                {
                    painter.Paint(Pixels, region);
                    _painted.Enqueue(region);

                    // One hand-over posted at a time. Each one takes every region finished by the
                    // time it runs, so the owner's thread puts them up in one go rather than once
                    // per region.
                    if (Interlocked.Exchange(ref _handOverPosted, 1) == 0)
                    {
                        _owner.Post(_ => HandOver(), null);
                    }
                });

            _owner.Post(_ => Arrive(), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded, which the owner's thread has already acted on. The picture it was drawing
            // is not wanted, so there is nothing more to do with it.
        }
    }

    private void Begin(ExploreSurface surface)
    {
        if (IsSuperseded)
        {
            return;
        }

        Surface = surface;
        _target.Begin(this);
    }

    /// <summary>Put every region painted so far on screen.</summary>
    private void HandOver()
    {
        // Cleared before the queue is emptied, so a region finished from here on posts a hand-over
        // of its own rather than waiting in the queue for one that has already looked.
        Volatile.Write(ref _handOverPosted, 0);

        if (IsSuperseded)
        {
            return;
        }

        List<CanvasRegion>? regions = null;

        while (_painted.TryDequeue(out var region))
        {
            (regions ??= []).Add(region);
        }

        if (regions is null)
        {
            return;
        }

        _landed.AddRange(regions);
        _target.Land(this, regions);
    }

    private void Arrive()
    {
        if (IsSuperseded)
        {
            return;
        }

        // The last regions can still be queued: their hand-over is posted but has not run, and it
        // will find nothing to do once this has.
        HandOver();

        IsArrived = true;
        _landed.Clear();
        _target.Arrive(this);
    }
}
