using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// The keyboard on the map's shapes: whether it has the map, which shape it is on (kept by
/// <see cref="ExploreFocus"/>), and how it comes onto them, by an arrow, by the map taking the focus or
/// by the menu key, as <see cref="MapFocusEntry"/> decides. Every way onto a shape ends in
/// <see cref="Landed"/>, which the map answers by marking the shape, naming it, bringing it into view
/// and telling a screen reader.
///
/// <para>Its nodes are numbers in the tree the page handed over last, and each shape is found in the
/// drawing of that tree on screen, which the map passes in, so a key acts on what the screen shows
/// (§7.1).</para>
/// </summary>
/// <param name="picked">What the user picked, which the keyboard starts on where it is one node.</param>
/// <param name="folderOf">The folder whose shapes hold a node's, or null for the top of the picture.</param>
internal sealed class ExploreKeyboard(IReadOnlySet<int> picked, Func<int, int?> folderOf)
{
    private readonly ExploreFocus _focus = new();

    /// <summary>Raised as the keyboard lands on a shape, by whichever way it came.</summary>
    public event EventHandler<ExploreKeyboardLanding>? Landed;

    /// <summary>
    /// Whether the map has the focus from the keyboard, or from a screen reader, rather than from a click:
    /// what marks the shape the keyboard is on and makes Enter open it. A click focuses the map too, so
    /// the keys go to it, and marks nothing until a key moves between the shapes.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>The folder whose shapes the keyboard moves between, or null for the top of the picture.</summary>
    public int? Folder => _focus.Folder;

    /// <summary>The shape the keyboard is on, while it has the map, and null otherwise.</summary>
    public (int? Folder, int Node)? On => IsActive && _focus.Node is { } node ? (_focus.Folder, node) : null;

    /// <summary>The one node picked, which the keyboard starts on where it was not moving.</summary>
    private int? PickedAlone => picked.Count == 1 ? picked.First() : null;

    /// <summary>
    /// The shape <paramref name="on"/> names, as <paramref name="drawing"/> draws it, or null where it
    /// does not.
    /// </summary>
    public static MapShape? Drawn(ExploreSurface drawing, (int? Folder, int Node) on) =>
        drawing.Shapes(on.Folder) is var shapes && MapShapes.IndexOf(shapes, on.Node) is { } index ? shapes[index] : null;

    /// <summary>
    /// The shape the keyboard is on in <paramref name="drawing"/>, while it has the map, and null
    /// otherwise. Puts it down there, as <see cref="ExploreFocus.Land"/> does.
    /// </summary>
    public MapShape? ShapeIn(ExploreSurface? drawing) =>
        IsActive && drawing is not null && _focus.Land(drawing) is { } found ? found.Shapes[found.Index] : null;

    /// <summary>
    /// An arrow key on the map: move to the shape next to the one the keyboard is on in
    /// <paramref name="direction"/>, and pick it, or stay where there is none that way. False where
    /// <paramref name="drawing"/> has no shapes to move between, so the key goes on to the page.
    /// </summary>
    public bool Arrow(ExploreSurface? drawing, MapDirection direction) =>
        Enter(MapFocusEntry.ByArrow(IsActive, PickedAlone), drawing, direction) is not null;

    /// <summary>The map has the focus from the keyboard or a screen reader: land on the shape the keyboard was on, or the one picked, or the first.</summary>
    public void TakeFocus(ExploreSurface? drawing)
    {
        IsActive = true;
        Enter(MapFocusEntry.OnFocus(_focus.Node, PickedAlone), drawing, direction: null);
    }

    /// <summary>
    /// The menu key on the map: land on the shape the menu opens on, picked first, and give it; or null,
    /// for no menu, where <see cref="MapFocusEntry.ByMenu"/> says the key does nothing.
    /// </summary>
    public MapShape? Menu(ExploreSurface drawing) =>
        MapFocusEntry.ByMenu(IsActive, PickedAlone) is { } entry ? Enter(entry, drawing, direction: null) : null;

    /// <summary>
    /// The keyboard takes the map on <paramref name="shape"/> of <paramref name="folder"/>, as a screen
    /// reader asks, before the map takes the focus: whatever the focus starts finds it there.
    /// </summary>
    public void TakeAt(ExploreSurface drawing, int? folder, MapShape shape)
    {
        IsActive = true;
        _focus.Set(drawing, folder, shape);
    }

    /// <summary>Remember that the keyboard is on <paramref name="shape"/> of <paramref name="folder"/>, without landing on it.</summary>
    public void Remember(ExploreSurface drawing, int? folder, MapShape shape) => _focus.Set(drawing, folder, shape);

    /// <summary>
    /// Land the keyboard on <paramref name="shape"/> of <paramref name="folder"/> in
    /// <paramref name="drawing"/>, and pick it where <paramref name="pick"/> says.
    /// </summary>
    public void Land(ExploreSurface drawing, int? folder, MapShape shape, bool pick)
    {
        IsActive = true;
        _focus.Set(drawing, folder, shape);

        Landed?.Invoke(this, new ExploreKeyboardLanding(drawing, folder, shape, pick));
    }

    /// <summary>The pointer or another control has the map's input now. False where the keyboard did not have it.</summary>
    public bool Leave()
    {
        var had = IsActive;

        IsActive = false;
        return had;
    }

    /// <summary>
    /// Where the keyboard is in the picture handed over, <paramref name="tree"/> at <paramref name="node"/>,
    /// from <paramref name="shown"/> at <paramref name="shownNode"/>: on the same shapes in a new tree of
    /// the same place, read through their counterparts; on the folder it came out of, in a folder above
    /// it; on the root, where the root is drawn again beside its volume; and on the first shape of
    /// anything else. Gives the counterparts it read through, or null where the picture is another place.
    /// </summary>
    public Func<int, int?>? Carry(ISizedTree? shown, int shownNode, ISizedTree? tree, int node, bool beside, bool samePlace)
    {
        if (samePlace && shown is ITrackedTree tracked && tree is not null && tracked.CounterpartsIn(tree) is { } counterpart)
        {
            _focus.Carry(counterpart);
            return counterpart;
        }

        if (tree is not null && ReferenceEquals(tree, shown) && MapFolders.CameUpFrom(tree, shownNode, node, beside) is { } left)
        {
            _focus.Expect(null, left);
        }
        else
        {
            _focus.Clear();
        }

        return null;
    }

    /// <summary>
    /// The one way onto a shape: start where <paramref name="entry"/> says, find that shape in
    /// <paramref name="drawing"/>, go on in <paramref name="direction"/> where the entry moves, and land.
    /// Gives the shape landed on, or null where there was none or the entry does not act on it.
    /// </summary>
    private MapShape? Enter(MapFocusEntry entry, ExploreSurface? drawing, MapDirection? direction)
    {
        if (entry.StartsOn is { } start)
        {
            _focus.Expect(folderOf(start), start);
        }

        if (drawing is null || _focus.Land(drawing) is not { } found || !entry.Accepts(found.Shapes[found.Index]))
        {
            return null;
        }

        var next = entry.Moves && direction is { } way
            ? MapNeighbours.Toward(found.Shapes, found.Index, way, drawing.Width, drawing.Height)
            : null;
        var shape = found.Shapes[next ?? found.Index];

        Land(drawing, _focus.Folder, shape, entry.Picks(moved: next is not null));

        return shape;
    }
}
