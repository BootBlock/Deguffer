using System.Buffers;
using System.Runtime.ExceptionServices;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The tiles of a map's picture wanted while it moves, each laid out and painted on a worker thread and
/// handed to the thread that wanted it as soon as it is finished, the most wanted first. A tile the GPU
/// shades is only laid out here, and handed over as the quads it shades.
///
/// <para>Off that thread for the reason <see cref="CanvasRedraws"/> gives: it is the UI thread, and the
/// camera keeps moving while the tiles are drawn. A tile is small, so it is one job rather than regions
/// of one, and several are drawn at once: as many as half the processors, on threads below the normal
/// priority. A tile is wanted only while the camera moves, which is exactly when the UI thread and the
/// compositor have frames to make, and the drawing of the whole picture a move asks for runs beside
/// them. Workers at the normal priority, one fewer than the processors as a redraw's are, took every
/// core between them and the redraw, and the screen stopped for a sixth of a second at a time.</para>
///
/// <para>What is wanted changes at every report of the camera, so each ask replaces the last. A tile no
/// longer wanted that no worker has started is never drawn, and one already started is finished and
/// handed over, because it is a tile of the same picture and the cache keeps it. Only a stop, for a
/// picture that has changed, drops what is under way: nothing started before it is handed over.</para>
/// </summary>
public sealed class MapTileDraws
{
    private readonly SynchronizationContext _owner;

    private readonly Action<MapTile, byte[]> _drawn;

    private readonly Action<MapTile, CushionQuads, TileColour> _shaded;

    private readonly int _workers;

    /// <summary>Guards everything below it, which the workers and the owner's thread both read.</summary>
    private readonly Lock _gate = new();

    /// <summary>The tiles wanted and not yet started, most wanted first.</summary>
    private readonly Queue<MapTileKey> _wanted = new();

    /// <summary>The tiles a worker is drawing, so an ask that wants one again does not draw it twice.</summary>
    private readonly HashSet<MapTileKey> _drawing = [];

    private TileSource? _source;

    /// <summary>A count of the stops, so a tile started before one is never handed over after it.</summary>
    private int _generation;

    private int _running;

    /// <param name="owner">The thread tiles are wanted on, and handed to.</param>
    /// <param name="drawn">
    /// Told of each tile painted on the CPU, on the owner's thread, with its pixels, a buffer at least
    /// the tile's size. The buffer is used again once this returns, so whatever it holds is put on screen
    /// before then.
    /// </param>
    /// <param name="shaded">Told of each tile for the GPU to shade, on the owner's thread, with its quads and the ground they are shaded on.</param>
    public MapTileDraws(SynchronizationContext owner, Action<MapTile, byte[]> drawn, Action<MapTile, CushionQuads, TileColour> shaded)
        : this(owner, drawn, shaded, Math.Max(1, Environment.ProcessorCount / 2))
    {
    }

    /// <param name="workers">How many tiles are drawn at once. One makes the order they are handed over in the order they were wanted, which is what a test can hold to.</param>
    internal MapTileDraws(
        SynchronizationContext owner,
        Action<MapTile, byte[]> drawn,
        Action<MapTile, CushionQuads, TileColour> shaded,
        int workers)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentNullException.ThrowIfNull(shaded);

        _owner = owner;
        _drawn = drawn;
        _shaded = shaded;
        _workers = workers;
    }

    /// <summary>
    /// Draw <paramref name="keys"/> of <paramref name="picture"/>'s picture, cut by
    /// <paramref name="tiling"/>, on <paramref name="ground"/>: the first of them first, and none that is
    /// being drawn already. Replaces whatever was wanted before and not yet started.
    /// </summary>
    /// <param name="shade">
    /// Whether the GPU shades the tiles, so a worker only lays each out and hands over its quads, or the
    /// CPU paints them, so a worker paints each too.
    /// </param>
    public void Want(TiledSurface picture, MapTiling tiling, TileColour ground, bool shade, IReadOnlyList<MapTileKey> keys)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(tiling);
        ArgumentNullException.ThrowIfNull(keys);

        int start;

        lock (_gate)
        {
            _source = new TileSource(picture, tiling, ground, shade);
            _wanted.Clear();

            foreach (var key in keys)
            {
                if (!_drawing.Contains(key))
                {
                    _wanted.Enqueue(key);
                }
            }

            start = Math.Min(_workers - _running, _wanted.Count);
            _running += Math.Max(0, start);
        }

        for (var i = 0; i < start; i++)
        {
            // A thread of its own, so its priority can be lowered without lowering a pool thread's.
            // A layout or a paint that fails is a defect, so it is raised on the owner's thread, where
            // the application's handling of an unexpected failure sees it, rather than lost with the task.
            Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
                .ContinueWith(
                    failed => _owner.Post(
                        state => ((ExceptionDispatchInfo)state!).Throw(),
                        ExceptionDispatchInfo.Capture(failed.Exception!.Flatten().InnerException!)),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.DenyChildAttach,
                    TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Want nothing more, and hand over nothing already under way, for a picture that has changed or a
    /// map that no longer moves.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _generation++;
            _source = null;
            _wanted.Clear();
            _drawing.Clear();
        }
    }

    /// <summary>
    /// Stop wanting the tiles not yet started, and go on handing over those under way: for a camera that
    /// has come to rest, whose picture is drawn whole now, and whose cores that drawing wants.
    /// </summary>
    public void Rest()
    {
        lock (_gate)
        {
            _wanted.Clear();
        }
    }

    /// <summary>Draw tiles until none is wanted, below the normal priority.</summary>
    private void Work()
    {
        var finished = false;
        var priority = Thread.CurrentThread.Priority;

        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

        try
        {
            while (true)
            {
                MapTileKey key;
                TileSource source;
                int generation;

                lock (_gate)
                {
                    if (_source is null || !_wanted.TryDequeue(out key))
                    {
                        _running--;
                        finished = true;
                        return;
                    }

                    source = _source;
                    generation = _generation;
                    _drawing.Add(key);
                }

                var tile = source.Picture.Tile(source.Tiling, key);

                if (source.Shades)
                {
                    var quads = tile.Quads();

                    _owner.Post(_ => HandOver(tile, quads, source.Ground, generation), null);
                    continue;
                }

                var pixels = ArrayPool<byte>.Shared.Rent((int)tile.Bytes);

                tile.Painter(source.Ground).Paint(pixels, new CanvasRegion(0, 0, tile.Width, tile.Height));

                _owner.Post(_ => HandOver(tile, pixels, generation), null);
            }
        }
        finally
        {
            Thread.CurrentThread.Priority = priority;

            // A layout or a paint that failed is a defect, raised on the owner's thread by the task's
            // continuation. The worker it took is free again either way.
            if (!finished)
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }
    }

    /// <summary>Hand <paramref name="tile"/> to the owner, on its thread, unless a stop came since it was started.</summary>
    private void HandOver(MapTile tile, byte[] pixels, int generation)
    {
        try
        {
            if (IsCurrent(tile, generation))
            {
                _drawn(tile, pixels);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    /// <summary>Hand <paramref name="tile"/> to the owner to shade, as <see cref="HandOver(MapTile, byte[], int)"/> does a painted one.</summary>
    private void HandOver(MapTile tile, CushionQuads quads, TileColour ground, int generation)
    {
        if (IsCurrent(tile, generation))
        {
            _shaded(tile, quads, ground);
        }
    }

    /// <summary>
    /// Whether no stop came since <paramref name="tile"/> was started, at <paramref name="generation"/>;
    /// and if none did, it is no longer being drawn.
    /// </summary>
    private bool IsCurrent(MapTile tile, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            _drawing.Remove(tile.Key);
            return true;
        }
    }

    /// <summary>What every tile wanted is a tile of, the ground it is painted on, and whether the GPU shades it instead.</summary>
    private sealed record TileSource(TiledSurface Picture, MapTiling Tiling, TileColour Ground, bool Shades);
}
