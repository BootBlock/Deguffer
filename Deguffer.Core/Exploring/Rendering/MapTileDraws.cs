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
/// <para><b>Started once, and parked between asks.</b> A tile takes well under a millisecond to lay
/// out, so the workers empty what is wanted within the frame that wanted it, and the next frame wants
/// more. A worker started for each ask was a new thread for each, and the UI thread waited for every
/// one to be scheduled: up to 2 ms a frame for 16 of them, and a frame of tens of milliseconds where the
/// machine was busy. So each worker is started the first time an ask has work for it, waits on a
/// semaphore once nothing is wanted, and an ask only queues its tiles and wakes as many as it needs.
/// They end at <see cref="Release"/>, for a map nobody sees, and the next ask starts them again.</para>
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

    private readonly Action<Action> _start;

    /// <summary>Guards everything below it, which the workers and the owner's thread both read.</summary>
    private readonly Lock _gate = new();

    /// <summary>The tiles wanted and not yet started, most wanted first.</summary>
    private readonly Queue<MapTileKey> _wanted = new();

    /// <summary>The tiles a worker is drawing, so an ask that wants one again does not draw it twice.</summary>
    private readonly HashSet<MapTileKey> _drawing = [];

    private TileSource? _source;

    /// <summary>A count of the stops, so a tile started before one is never handed over after it.</summary>
    private int _generation;

    /// <summary>The workers started since the last <see cref="Release"/>, and none before the first ask that has work for them.</summary>
    private Crew? _crew;

    /// <param name="owner">The thread tiles are wanted on, and handed to.</param>
    /// <param name="drawn">
    /// Told of each tile painted on the CPU, on the owner's thread, with its pixels, a buffer at least
    /// the tile's size. The buffer is used again once this returns, so whatever it holds is put on screen
    /// before then.
    /// </param>
    /// <param name="shaded">Told of each tile for the GPU to shade, on the owner's thread, with its quads and the ground they are shaded on.</param>
    public MapTileDraws(SynchronizationContext owner, Action<MapTile, byte[]> drawn, Action<MapTile, CushionQuads, TileColour> shaded)
        : this(owner, drawn, shaded, Math.Max(1, Environment.ProcessorCount / 2), StartThread)
    {
    }

    /// <param name="workers">How many tiles are drawn at once. One makes the order they are handed over in the order they were wanted, which is what a test can hold to.</param>
    /// <param name="start">Runs a worker on a thread of its own, the seam a test counts the threads started through.</param>
    internal MapTileDraws(
        SynchronizationContext owner,
        Action<MapTile, byte[]> drawn,
        Action<MapTile, CushionQuads, TileColour> shaded,
        int workers,
        Action<Action> start)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(drawn);
        ArgumentNullException.ThrowIfNull(shaded);

        _owner = owner;
        _drawn = drawn;
        _shaded = shaded;
        _workers = workers;
        _start = start;
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

        Crew crew;
        int wake;
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

            crew = _crew ??= new Crew();

            // The parked first, and a new worker only for what they leave, so each is started once.
            wake = Math.Min(crew.Parked, _wanted.Count);
            crew.Parked -= wake;
            start = Math.Min(_workers - crew.Started, _wanted.Count - wake);
            crew.Started += Math.Max(0, start);
        }

        if (wake > 0)
        {
            crew.Wake.Release(wake);
        }

        for (var i = 0; i < start; i++)
        {
            _start(() => Work(crew));
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

    /// <summary>
    /// <see cref="Stop"/>, and end the workers, for a map nobody sees: a closed or hidden one holds no
    /// threads. One drawing a tile ends once it has finished it. The next ask starts them again.
    /// </summary>
    public void Release()
    {
        Crew? crew;
        int wake;

        Stop();

        lock (_gate)
        {
            crew = _crew;
            _crew = null;

            if (crew is null)
            {
                return;
            }

            crew.Ended = true;
            wake = crew.Parked;
            crew.Parked = 0;
        }

        if (wake > 0)
        {
            crew.Wake.Release(wake);
        }
    }

    /// <summary>
    /// A thread of its own for a worker, so its priority can be lowered without lowering a pool
    /// thread's. Unsafe, so a worker does not keep the context of whichever ask happened to start it.
    /// </summary>
    private static void StartThread(Action work) =>
        new Thread(work.Invoke) { IsBackground = true, Name = "Deguffer map tiles" }.UnsafeStart();

    /// <summary>Draw tiles as they are wanted, parked while none is, until <paramref name="crew"/> ends.</summary>
    private void Work(Crew crew)
    {
        // Lowered here rather than before the thread starts: a start waits until the thread is first
        // scheduled, and one started below the normal priority on a busy machine kept the asking
        // thread waiting for seconds.
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

        while (Next(crew) is var (key, source, generation))
        {
            try
            {
                Draw(key, source, generation);
            }
            catch (Exception failure)
            {
                // A layout or a paint that fails is a defect, so it is raised on the owner's thread,
                // where the application's handling of an unexpected failure sees it. Escaping here it
                // would end the process, and the worker stays for the tiles after it.
                _owner.Post(static state => ((ExceptionDispatchInfo)state!).Throw(), ExceptionDispatchInfo.Capture(failure));
            }
        }
    }

    /// <summary>
    /// The next tile wanted, with the picture it is of and the stops counted when it was started, waiting
    /// parked while none is; or nothing once <paramref name="crew"/> has ended.
    /// </summary>
    private (MapTileKey Key, TileSource Source, int Generation)? Next(Crew crew)
    {
        while (true)
        {
            lock (_gate)
            {
                if (crew.Ended)
                {
                    return null;
                }

                if (_source is { } source && _wanted.TryDequeue(out var key))
                {
                    _drawing.Add(key);
                    return (key, source, _generation);
                }

                crew.Parked++;
            }

            // Woken by an ask, which counted this off the parked, or by the release that ended it. A
            // worker woken for a tile another took first is parked again.
            crew.Wake.Wait();
        }
    }

    /// <summary>Lay out <paramref name="key"/>'s tile, paint it unless the GPU shades it, and post it to the owner.</summary>
    private void Draw(MapTileKey key, TileSource source, int generation)
    {
        var tile = source.Picture.Tile(source.Tiling, key);

        if (source.Shades)
        {
            var quads = tile.Quads();

            _owner.Post(_ => HandOver(tile, quads, source.Ground, generation), null);
            return;
        }

        var pixels = ArrayPool<byte>.Shared.Rent((int)tile.Bytes);

        tile.Painter(source.Ground).Paint(pixels, new CanvasRegion(0, 0, tile.Width, tile.Height));

        _owner.Post(_ => HandOver(tile, pixels, generation), null);
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

    /// <summary>
    /// The workers started between two releases, and the semaphore they park on. Each set has a
    /// semaphore of its own, so a wake meant for one that has ended can never be taken by one started
    /// after it, leaving an old one parked for good. Every field is guarded by the gate.
    /// </summary>
    private sealed class Crew
    {
        public SemaphoreSlim Wake { get; } = new(initialCount: 0);

        public int Started { get; set; }

        /// <summary>How many wait on <see cref="Wake"/> and have not been counted off by a wake.</summary>
        public int Parked { get; set; }

        public bool Ended { get; set; }
    }
}
