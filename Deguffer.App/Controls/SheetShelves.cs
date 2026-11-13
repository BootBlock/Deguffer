namespace Deguffer.App.Controls;

/// <summary>
/// Where each name's text goes on the one surface every name on the map is drawn into (see
/// <see cref="NameSheet"/>): rectangles handed out in shelves, rows of one height, and taken back as
/// names change size or are put away.
///
/// <para>Shelves, because names come in very few heights: one line of the application's type, and one
/// to three lines of SpaceMonger's at each spacing. A name is put on a shelf of its own height first,
/// so the sheet packs tightly without a general packer's search. A rectangle given back joins the free
/// spans beside it on its shelf, so a name that changes size takes back the room it left.</para>
///
/// <para>Each rectangle keeps a pixel clear to its right and below it. A name moved off the pixel grid
/// samples a little past its edges, and would otherwise show a sliver of its neighbour.</para>
///
/// <para>Separate from <see cref="NameSheet"/>, which owns the surface and draws into it, because this
/// is arithmetic a test proves without a device (G1).</para>
/// </summary>
/// <param name="width">How wide the sheet is, in pixels.</param>
/// <param name="height">How tall the sheet is, in pixels.</param>
internal sealed class SheetShelves(int width, int height)
{
    /// <summary>The clear pixel kept to the right of and below each rectangle.</summary>
    public const int Gutter = 1;

    /// <summary>
    /// How much taller than a name a shelf of another height can be and still take it: twice, so a name
    /// of one line can go on a shelf of two when there is no other room, and no taller.
    /// </summary>
    private const int TallestFit = 2;

    private readonly List<Shelf> _shelves = [];

    /// <summary>The top of the sheet not yet given to any shelf.</summary>
    private int _bottom;

    /// <summary>The widest rectangle the sheet can hand out, in pixels.</summary>
    public int Width => width - Gutter;

    /// <summary>
    /// A rectangle <paramref name="wide"/> by <paramref name="high"/> pixels, or null where the sheet has
    /// no room left for one. Only a sheet emptied (<see cref="Clear"/>) has room again for a name that
    /// found none.
    /// </summary>
    public SheetRegion? Take(int wide, int high)
    {
        if (wide <= 0 || high <= 0 || wide > Width || high > height - Gutter)
        {
            return null;
        }

        var span = wide + Gutter;
        var rowHeight = high + Gutter;

        if (Fitting(span, rowHeight, exact: true) is { } exact)
        {
            return exact.Take(span, high);
        }

        if (_bottom + rowHeight <= height)
        {
            var shelf = new Shelf(_bottom, rowHeight, width);
            _shelves.Add(shelf);
            _bottom += rowHeight;

            return shelf.Take(span, high);
        }

        return Fitting(span, rowHeight, exact: false)?.Take(span, high);
    }

    /// <summary>Take back <paramref name="region"/>, which this sheet handed out and has not been emptied since.</summary>
    public void Give(SheetRegion region)
    {
        Shelf? shelf = null;

        foreach (var row in _shelves)
        {
            if (row.Y == region.Y)
            {
                shelf = row;
                break;
            }
        }

        if (shelf is null)
        {
            throw new ArgumentException($"No shelf of this sheet starts at {region.Y}.", nameof(region));
        }

        shelf.Give(region.X, region.Width + Gutter);

        // An empty shelf at the bottom goes back to the sheet, so a shelf of another height can start
        // there. One in the middle stays, and takes its height again.
        while (_shelves.Count > 0 && _shelves[^1].IsEmpty)
        {
            _bottom = _shelves[^1].Y;
            _shelves.RemoveAt(_shelves.Count - 1);
        }
    }

    /// <summary>Take back every rectangle at once, for a sheet drawn again from empty.</summary>
    public void Clear()
    {
        _shelves.Clear();
        _bottom = 0;
    }

    /// <summary>
    /// The shelf a rectangle <paramref name="span"/> wide goes on, of exactly <paramref name="rowHeight"/>
    /// where <paramref name="exact"/>, and otherwise the lowest shelf tall enough within
    /// <see cref="TallestFit"/>.
    /// </summary>
    private Shelf? Fitting(int span, int rowHeight, bool exact)
    {
        Shelf? best = null;

        foreach (var shelf in _shelves)
        {
            var fits = exact
                ? shelf.Height == rowHeight
                : shelf.Height > rowHeight && shelf.Height <= rowHeight * TallestFit;

            if (fits && (best is null || shelf.Height < best.Height) && shelf.HasRoom(span))
            {
                best = shelf;

                if (exact)
                {
                    break;
                }
            }
        }

        return best;
    }

    /// <summary>One row of the sheet, and the spans of it nothing holds, in order across it.</summary>
    private sealed class Shelf
    {
        private readonly List<(int X, int Width)> _free;

        private readonly int _width;

        public Shelf(int y, int height, int width)
        {
            Y = y;
            Height = height;
            _width = width;
            _free = [(0, width)];
        }

        public int Y { get; }

        public int Height { get; }

        public bool IsEmpty => _free is [(0, var width)] && width == _width;

        public bool HasRoom(int span) => FirstOf(span) >= 0;

        /// <summary>The leftmost free span at least <paramref name="span"/> wide, cut from its start.</summary>
        public SheetRegion Take(int span, int high)
        {
            var at = FirstOf(span);
            var (x, width) = _free[at];

            if (width == span)
            {
                _free.RemoveAt(at);
            }
            else
            {
                _free[at] = (x + span, width - span);
            }

            return new SheetRegion(x, Y, span - Gutter, high);
        }

        /// <summary>Free <paramref name="span"/> pixels from <paramref name="x"/>, joined to the free spans either side.</summary>
        public void Give(int x, int span)
        {
            var at = 0;

            while (at < _free.Count && _free[at].X < x)
            {
                at++;
            }

            _free.Insert(at, (x, span));

            if (at + 1 < _free.Count && _free[at].X + _free[at].Width == _free[at + 1].X)
            {
                _free[at] = (_free[at].X, _free[at].Width + _free[at + 1].Width);
                _free.RemoveAt(at + 1);
            }

            if (at > 0 && _free[at - 1].X + _free[at - 1].Width == _free[at].X)
            {
                _free[at - 1] = (_free[at - 1].X, _free[at - 1].Width + _free[at].Width);
                _free.RemoveAt(at);
            }
        }

        /// <summary>The index of the leftmost free span at least <paramref name="span"/> wide, or -1 where there is none.</summary>
        private int FirstOf(int span)
        {
            for (var i = 0; i < _free.Count; i++)
            {
                if (_free[i].Width >= span)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}

/// <summary>A rectangle of the names' sheet, in pixels (see <see cref="SheetShelves"/>).</summary>
internal readonly record struct SheetRegion(int X, int Y, int Width, int Height);
