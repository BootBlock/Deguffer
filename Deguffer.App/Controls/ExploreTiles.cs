using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The tiles of one picture a moving map shows, so what comes into view is as sharp as the zoom it is
/// seen at: drawn a tile at a time as the camera moves (<see cref="MapTileDraws"/>), each coming in with
/// a short crossfade, and kept while they fit (<see cref="MapTileCache{T}"/>), so going back over a part
/// of the picture already drawn shows it sharp at once. Which tiles, and in what order, is
/// <see cref="MapTiling"/>'s.
///
/// <para>Shown only while the camera moves, and until the drawing it comes to rest at has arrived. At
/// rest that drawing covers the screen, names its shapes and answers the pointer, and a tile drawn for a
/// deeper zoom would show more detail than it has.</para>
///
/// <para>Each level is a visual of its own, coarser under finer, in one of two places: <see cref="Over"/>
/// the drawing the map works from where the level is drawn more finely than it, and <see cref="Under"/>
/// it otherwise, where it shows only past that drawing's edges (<see cref="MapTiling.Over"/>). Only the
/// levels down to the zoom on screen are shown: a deeper level's tiles would draw folders the zoom has
/// not opened.</para>
///
/// <para>Separate from <see cref="ExploreLayers"/>, which keeps whole drawings, because a tile is a piece
/// of a picture rather than a picture, cached and placed by its own rules (G1). The layers stack these
/// two visuals round the drawing on top.</para>
/// </summary>
internal sealed class ExploreTiles
{
    /// <summary>How many tiles let go keep their surface to be written again, rather than giving its memory back (G5).</summary>
    private const int Spare = 16;

    private readonly Compositor _compositor;

    private readonly MapGraphics _graphics;

    private readonly IMotionPolicy _motion;

    private readonly MapTileDraws _draws;

    private readonly Dictionary<int, ContainerVisual> _levels = [];

    private readonly Stack<TileSprite> _spare = new();

    private MapTiling? _tiling;

    private MapTileCache<TileSprite>? _cache;

    /// <summary>The deepest level shown: that of the zoom on screen.</summary>
    private int _deepest = -1;

    /// <summary>The zoom of the drawing the map works from, which the levels are stacked over and under.</summary>
    private MapViewport _drawn;

    private (double Width, double Height) _size;

    private MapOrigin _origin;

    /// <summary>Whether a tile that answered the pointer has been let go of since that was last said.</summary>
    private bool _answerLost;

    public ExploreTiles(Compositor compositor, MapGraphics graphics, IMotionPolicy motion, SynchronizationContext owner)
    {
        _compositor = compositor;
        _graphics = graphics;
        _motion = motion;
        _draws = new MapTileDraws(owner, Arrived);

        Over = compositor.CreateContainerVisual();
        Under = compositor.CreateContainerVisual();
        Over.IsVisible = false;
        Under.IsVisible = false;
    }

    /// <summary>The levels drawn more finely than the drawing the map works from, which go over it.</summary>
    public ContainerVisual Over { get; }

    /// <summary>The levels drawn no more finely than it, which go under it.</summary>
    public ContainerVisual Under { get; }

    /// <summary>Whether the tiles are on screen.</summary>
    public bool IsShown => Over.IsVisible;

    /// <summary>
    /// Which tiles answer the pointer has changed: one has finished coming in, or one that answered has
    /// gone, so what the pointer is over may be something else although neither it nor the camera moved.
    /// </summary>
    public event EventHandler? AnswersChanged;

    /// <summary>
    /// Show the tiles of <paramref name="picture"/>'s picture for a screen showing
    /// <paramref name="shown"/> on its way to <paramref name="target"/>, and draw those still missing on
    /// <paramref name="ground"/>: the screen's first, from the one at <paramref name="focusX"/>,
    /// <paramref name="focusY"/> of the picture outwards, then the target's.
    /// </summary>
    public void Want(TiledSurface picture, MapViewport shown, MapViewport target, double focusX, double focusY, TileColour ground)
    {
        if (_tiling is not { } tiling || tiling.Width != picture.Width || tiling.Height != picture.Height)
        {
            Forget();
            tiling = _tiling = new MapTiling(picture.Width, picture.Height);
            _cache = new MapTileCache<TileSprite>(tiling.Budget, Recycle);
        }

        var level = MapTiling.LevelOf(shown.Zoom);
        var wanted = new List<MapTileKey>();

        Missing(tiling.Covering(shown, level, focusX, focusY), wanted);

        if (MapTiling.LevelOf(target.Zoom) is var going && (going != level || target != shown))
        {
            var (x, y) = target.PictureAt(0.5, 0.5);

            Missing(tiling.Covering(target, going, x, y), wanted);
        }

        ShowDownTo(level);

        Over.IsVisible = true;
        Under.IsVisible = true;

        _draws.Want(picture, tiling, ground, wanted);
    }

    /// <summary>The camera has come to rest: draw nothing more, but keep what is under way, and go on showing.</summary>
    public void Rest() => _draws.Rest();

    /// <summary>The drawing the camera came to rest at has arrived over the tiles, which go until the next move.</summary>
    public void Hide()
    {
        _draws.Rest();
        Over.IsVisible = false;
        Under.IsVisible = false;
    }

    /// <summary>
    /// Stack the levels over and under a drawing the map works from, made at <paramref name="drawn"/>.
    /// </summary>
    public void Stack(MapViewport drawn)
    {
        _drawn = drawn;

        foreach (var (level, visual) in _levels)
        {
            Place(level, visual);
        }
    }

    /// <summary>
    /// The finest tile shown over the drawing the map works from at picture point (<paramref name="x"/>,
    /// <paramref name="y"/>), or null where none is: what the screen shows there (§7.1). A tile still
    /// coming in answers nothing, as a drawing fading in does not: the screen shows what is under it.
    /// </summary>
    public MapTile? TileOver(double x, double y)
    {
        if (!IsShown || _tiling is not { } tiling || _cache is not { } cache)
        {
            return null;
        }

        for (var level = _deepest; level >= 0 && MapTiling.Over(level, _drawn); level--)
        {
            if (tiling.At(level, x, y) is { } key && cache.TryGet(key, out var sprite) && sprite.Answers)
            {
                return sprite.Tile;
            }
        }

        return null;
    }

    /// <summary>Put every tile where its part of the picture lies, in a picture <paramref name="width"/> by <paramref name="height"/> measured from <paramref name="origin"/>.</summary>
    public void Fit(double width, double height, MapOrigin origin)
    {
        _size = (width, height);
        _origin = origin;

        foreach (var sprite in _cache?.Values ?? [])
        {
            Fit(sprite);
        }
    }

    /// <summary>
    /// Let go of every tile and draw none still under way, for a picture that has changed. The surfaces
    /// of a few are kept to be written again.
    /// </summary>
    public void Forget()
    {
        _draws.Stop();
        _cache?.Clear();
        Over.IsVisible = false;
        Under.IsVisible = false;

        RaiseIfAnswerLost();
    }

    /// <summary>Let go of every tile and every surface kept, for a map nobody can see.</summary>
    public void Release()
    {
        Forget();

        while (_spare.TryPop(out var sprite))
        {
            sprite.Dispose();
        }
    }

    /// <summary>
    /// Let go of everything, for a device that has replaced the one the surfaces were made on: they hold
    /// nothing, and a write into one would show nothing (see <see cref="MapGraphics.Replaced"/>).
    /// </summary>
    public void Renew() => Release();

    /// <summary>Add to <paramref name="wanted"/> each of <paramref name="keys"/> not kept, and count each kept as used.</summary>
    private void Missing(IReadOnlyList<MapTileKey> keys, List<MapTileKey> wanted)
    {
        foreach (var key in keys)
        {
            if (!_cache!.TryGet(key, out _))
            {
                wanted.Add(key);
            }
        }
    }

    /// <summary>Show the levels down to <paramref name="deepest"/>, and none deeper.</summary>
    private void ShowDownTo(int deepest)
    {
        if (deepest == _deepest)
        {
            return;
        }

        _deepest = deepest;

        foreach (var (level, visual) in _levels)
        {
            visual.IsVisible = level <= deepest;
        }
    }

    /// <summary>A tile has been drawn: put it on screen and keep it.</summary>
    private void Arrived(MapTile tile, byte[] pixels)
    {
        var sprite = _spare.TryPop(out var spare) ? spare : new TileSprite(_compositor, _graphics.CreateTileSurface());

        _graphics.Write(sprite.Surface, pixels, tile.Width, new CanvasRegion(0, 0, tile.Width, tile.Height));

        sprite.Tile = tile;
        sprite.Answers = false;
        sprite.Visual.Size = new Vector2(tile.Width, tile.Height);
        Fit(sprite);

        Level(tile.Key.Level).Children.InsertAtTop(sprite.Visual);

        // Kept before it comes in, so a tile it lets go of is gone before this one can answer.
        _cache!.Add(tile.Key, sprite, tile.Bytes);
        RaiseIfAnswerLost();

        FadeIn(sprite, tile);
    }

    /// <summary>
    /// Fade <paramref name="sprite"/> in over what is under it, and let it answer the pointer once it
    /// covers that, while it still shows <paramref name="tile"/>. The motion is asked for each time, so a
    /// reader who changes the setting is followed at the next tile.
    /// </summary>
    private void FadeIn(TileSprite sprite, MapTile tile)
    {
        var visual = sprite.Visual;
        var motion = _motion.For(MotionToken.Sharpen);

        visual.StopAnimation(nameof(Visual.Opacity));

        if (motion.IsInstant)
        {
            visual.Opacity = 1;
            Answer(sprite, tile);
            return;
        }

        var rise = _compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0, 0);
        rise.InsertKeyFrame(1, 1);
        rise.Duration = motion.Duration;

        var fading = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.Opacity = 0;
        visual.StartAnimation(nameof(Visual.Opacity), rise);
        fading.End();

        fading.Completed += (_, _) => Answer(sprite, tile);
    }

    /// <summary>
    /// Let <paramref name="sprite"/> answer the pointer, unless it has been let go of, or holds another
    /// tile now, since it began to come in.
    /// </summary>
    private void Answer(TileSprite sprite, MapTile tile)
    {
        if (sprite.Tile != tile)
        {
            return;
        }

        sprite.Answers = true;
        AnswersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Say that a tile which answered the pointer has gone, if one has since this was last said.</summary>
    private void RaiseIfAnswerLost()
    {
        if (!_answerLost)
        {
            return;
        }

        _answerLost = false;
        AnswersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The visual of <paramref name="level"/>, made the first time a tile of it arrives, stacked among the others.</summary>
    private ContainerVisual Level(int level)
    {
        if (_levels.TryGetValue(level, out var visual))
        {
            return visual;
        }

        visual = _compositor.CreateContainerVisual();
        visual.IsVisible = level <= _deepest;
        _levels[level] = visual;
        Place(level, visual);

        return visual;
    }

    /// <summary>Put the visual of <paramref name="level"/> over or under the drawing, above every coarser level there and below every finer one.</summary>
    private void Place(int level, ContainerVisual visual)
    {
        var into = MapTiling.Over(level, _drawn) ? Over : Under;

        visual.Parent?.Children.Remove(visual);

        var finer = _levels
            .Where(other => other.Key > level && other.Value.Parent == into)
            .OrderBy(other => other.Key)
            .Select(other => other.Value)
            .FirstOrDefault();

        if (finer is null)
        {
            into.Children.InsertAtTop(visual);
        }
        else
        {
            into.Children.InsertBelow(visual, finer);
        }
    }

    /// <summary>Put <paramref name="sprite"/> where its tile lies in the picture.</summary>
    private void Fit(TileSprite sprite)
    {
        var placed = sprite.Tile!.Canvas(_size.Width, _size.Height, _origin);

        sprite.Visual.Scale = new Vector3((float)placed.ScaleX, (float)placed.ScaleY, 1);
        sprite.Visual.Offset = new Vector3((float)placed.X, (float)placed.Y, 0);
    }

    /// <summary>
    /// A tile let go: off the screen, and its surface kept to be written again, or given back. One that
    /// answered the pointer is said to have gone once the cache is done with it, not while it is still
    /// making room.
    /// </summary>
    private void Recycle(TileSprite sprite)
    {
        sprite.Visual.Parent?.Children.Remove(sprite.Visual);
        _answerLost |= sprite.Answers;
        sprite.Tile = null;
        sprite.Answers = false;

        if (_spare.Count < Spare)
        {
            _spare.Push(sprite);
        }
        else
        {
            sprite.Dispose();
        }
    }

    /// <summary>What shows one tile: a visual painted by a surface of a tile's size, and the tile it holds now.</summary>
    private sealed class TileSprite : IDisposable
    {
        private readonly CompositionSurfaceBrush _brush;

        public TileSprite(Compositor compositor, CompositionDrawingSurface surface)
        {
            Surface = surface;
            _brush = compositor.CreateUnstretchedBrush(surface);
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = _brush;
        }

        public CompositionDrawingSurface Surface { get; }

        public SpriteVisual Visual { get; }

        public MapTile? Tile { get; set; }

        /// <summary>Whether <see cref="Tile"/> has come in in full, and so answers the pointer.</summary>
        public bool Answers { get; set; }

        public void Dispose()
        {
            Visual.Dispose();
            _brush.Dispose();
            Surface.Dispose();
        }
    }
}
