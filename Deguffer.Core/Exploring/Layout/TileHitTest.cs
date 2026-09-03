namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Answers "what is under the pointer" over a laid-out canvas.
///
/// <para>A uniform grid of cells, each holding the tiles that overlap it, in the same compressed-row
/// form <see cref="ExploreTree"/> uses for its children — one offsets array and one flat entries
/// array, rather than a list per cell. A treemap of a full volume runs to tens of thousands of
/// rectangles and a pointer moves at the display's refresh rate, so a scan of every tile per move
/// is work done sixty times a second for an answer that touches a handful of them (G4).</para>
///
/// <para>The tiles are nested, so the answer is the <em>deepest</em> one containing the point rather
/// than the first. Without that a pointer over a file inside a directory inside a volume reports the
/// volume, because the volume's rectangle contains every point on the canvas.</para>
/// </summary>
public sealed class TileHitTest
{
    /// <summary>
    /// Cell size in canvas pixels. Large enough that a full canvas is a few thousand cells rather
    /// than a million, small enough that a cell rarely holds more than a few tiles.
    /// </summary>
    private const int CellSize = 16;

    private readonly IReadOnlyList<ExploreTile> _tiles;
    private readonly int[] _cellStart;
    private readonly int[] _entries;
    private readonly int _columns;
    private readonly int _rows;

    public TileHitTest(IReadOnlyList<ExploreTile> tiles, float width, float height)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _tiles = tiles;
        _columns = Math.Max(1, (int)Math.Ceiling(width / CellSize));
        _rows = Math.Max(1, (int)Math.Ceiling(height / CellSize));

        var cells = _columns * _rows;
        _cellStart = new int[cells + 1];

        // Counting sort, as everywhere else in this codebase that inverts a many-to-many relation:
        // one pass to count, a prefix sum, one pass to place. A list per cell would allocate one
        // object per cell for a structure that never changes after construction.
        for (var i = 0; i < tiles.Count; i++)
        {
            foreach (var cell in CellsOf(tiles[i]))
            {
                _cellStart[cell + 1]++;
            }
        }

        for (var i = 0; i < cells; i++)
        {
            _cellStart[i + 1] += _cellStart[i];
        }

        _entries = new int[_cellStart[cells]];
        var cursor = new int[cells];

        for (var i = 0; i < tiles.Count; i++)
        {
            foreach (var cell in CellsOf(tiles[i]))
            {
                _entries[_cellStart[cell] + cursor[cell]++] = i;
            }
        }
    }

    /// <summary>
    /// The index into the tile list of the deepest rectangle covering this point, or null where the
    /// point is over nothing.
    /// </summary>
    public int? At(float x, float y)
    {
        if (x < 0 || y < 0)
        {
            return null;
        }

        var column = (int)(x / CellSize);
        var row = (int)(y / CellSize);

        if (column >= _columns || row >= _rows)
        {
            return null;
        }

        var cell = (row * _columns) + column;

        int? best = null;
        var deepest = int.MinValue;

        for (var i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
        {
            var candidate = _tiles[_entries[i]];

            if (candidate.Depth > deepest && Contains(candidate, x, y))
            {
                best = _entries[i];
                deepest = candidate.Depth;
            }
        }

        return best;
    }

    private static bool Contains(ExploreTile tile, float x, float y) =>
        x >= tile.X && x < tile.X + tile.Width && y >= tile.Y && y < tile.Y + tile.Height;

    /// <summary>Every cell this tile overlaps, clipped to the canvas.</summary>
    private IEnumerable<int> CellsOf(ExploreTile tile)
    {
        var left = Math.Max(0, (int)(tile.X / CellSize));
        var top = Math.Max(0, (int)(tile.Y / CellSize));

        // The right and bottom edges are exclusive, so a tile ending exactly on a cell boundary
        // must not claim the cell beyond it — subtracting an epsilon of a pixel is what keeps the
        // index agreeing with Contains rather than holding entries it will always reject.
        var right = Math.Min(_columns - 1, (int)((tile.X + tile.Width - 0.001f) / CellSize));
        var bottom = Math.Min(_rows - 1, (int)((tile.Y + tile.Height - 0.001f) / CellSize));

        for (var row = top; row <= bottom; row++)
        {
            for (var column = left; column <= right; column++)
            {
                yield return (row * _columns) + column;
            }
        }
    }
}
