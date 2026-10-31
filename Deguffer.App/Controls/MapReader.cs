using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Controls;

/// <summary>
/// One drawing of the map as a screen reader reads it (<see cref="MapAutomationPeer"/>): which shapes
/// it gives under which folder, what each is called and what it is, where it is on the screen, which
/// the keyboard is on and which are picked; and what a screen reader may do to a shape, as the
/// keyboard and the pointer do it.
///
/// <para>The map makes one for each drawing it reads (<see cref="ExploreMap.Reader"/>), so a peer kept
/// from an earlier drawing holds an earlier reader. What it reads of its own drawing stays true of
/// that drawing. What it does to the map is refused once another is on screen (<see cref="IsOnScreen"/>),
/// whose node numbers may be other nodes (§7.1).</para>
/// </summary>
/// <param name="map">The map, which acts on what a screen reader asks and holds what the page says of its tree.</param>
/// <param name="drawing">The drawing read, of the tree the page handed over last.</param>
/// <param name="keyboard">The keyboard on the map's shapes, whose place a screen reader reads and moves.</param>
/// <param name="picked">What the user picked, in the drawing's tree.</param>
/// <param name="zoom">The map's zoom, which places the drawing on the screen.</param>
internal sealed class MapReader(ExploreMap map, ExploreSurface drawing, ExploreKeyboard keyboard, IReadOnlySet<int> picked, ExploreZoom zoom)
{
    /// <summary>Whether this is the reader of the drawing on screen, so what it does acts on what the screen shows.</summary>
    public bool IsOnScreen => ReferenceEquals(map.Reader, this);

    /// <summary>Whether what is picked on this picture is acted on: not on a step of a replay (<see cref="MapReading.Answers"/>).</summary>
    public bool Answers => MapReading.Answers(drawing.Tree);

    /// <summary>Whether this reads <paramref name="other"/>.</summary>
    public bool Reads(ExploreSurface other) => ReferenceEquals(drawing, other);

    /// <summary>
    /// The shapes of <paramref name="folder"/>, or of the top of the picture for null: none where the page
    /// has given no facts to describe them with.
    /// </summary>
    public IReadOnlyList<MapShape> Shapes(int? folder) => Described ? drawing.Shapes(folder) : [];

    /// <summary>The shapes given under <paramref name="folder"/>'s own shape, none while it is collapsed. See <see cref="MapFolders.Listed"/>.</summary>
    /// <param name="expanded">The folders the reader expanded.</param>
    public IReadOnlyList<MapShape> Listed(int folder, IReadOnlySet<int> expanded) =>
        Described ? MapFolders.Listed(drawing, drawing.Root, folder, keyboard.Folder, expanded) : [];

    /// <summary>Whether <paramref name="folder"/>'s shapes are given. See <see cref="MapFolders.Lists"/>.</summary>
    /// <param name="expanded">The folders the reader expanded.</param>
    public bool Lists(int folder, IReadOnlySet<int> expanded) =>
        MapFolders.Lists(drawing.Tree, drawing.Root, drawing.HasVolumeBeside, folder, keyboard.Folder, expanded);

    /// <summary>The folder whose shapes hold <paramref name="node"/>'s, or null for the top of the picture. See <see cref="MapFolders.Of"/>.</summary>
    public int? FolderOf(int node) => MapFolders.Of(drawing.Tree, drawing.Root, drawing.HasVolumeBeside, node);

    /// <summary>The shape <paramref name="on"/> names, as the drawing draws it, or null where it does not.</summary>
    public MapShape? Drawn((int? Folder, int Node) on) => ExploreKeyboard.Drawn(drawing, on);

    /// <summary>What <paramref name="shape"/> of <paramref name="folder"/> is called. See <see cref="MapShapeText.Name(ExploreSurface, int?, MapShape, Func{int, MapShapeFacts?})"/>.</summary>
    public string NameOf(int? folder, MapShape shape) => MapShapeText.Name(drawing, folder, shape, FactsOf);

    /// <summary>What <paramref name="shape"/> is and what can be done with it. See <see cref="MapShapeText.Help(MapShape, Func{int, MapShapeFacts?}, Func{int, Core.Exploring.Acting.ExploreVerdict?})"/>.</summary>
    public string HelpOf(MapShape shape) => MapShapeText.Help(shape, FactsOf, node => map.Removal?.Invoke(drawing.Tree, node));

    /// <summary>Where <paramref name="shape"/> is on the screen now, in fractions of the map.</summary>
    public MapFrame OnScreenOf(MapShape shape) => drawing.PlacementOn(zoom.Shown).OnScreen(shape.Frame);

    /// <summary>The shape the keyboard is on, while it has the map, and null otherwise.</summary>
    public (int? Folder, int Node)? FocusedShape() => map.FocusState != FocusState.Unfocused ? keyboard.On : null;

    public bool HasFocusOn(int? folder, int node) => FocusedShape() == (folder, node);

    public bool IsPicked(int node) => picked.Contains(node);

    /// <summary>What is picked, each with the folder whose shapes hold it.</summary>
    public IEnumerable<(int? Folder, int Node)> PickedShapes() => picked.Select(node => (FolderOf(node), node));

    /// <summary>
    /// Put the keyboard on <paramref name="shape"/>, as a screen reader asks: the map takes the focus if
    /// it has not got it.
    /// </summary>
    public void Focus(int? folder, MapShape shape)
    {
        if (!IsOnScreen)
        {
            return;
        }

        keyboard.TakeAt(drawing, folder, shape);

        if (map.FocusState == FocusState.Unfocused)
        {
            map.Focus(FocusState.Programmatic);
        }

        keyboard.Land(drawing, folder, shape, pick: false);
    }

    /// <summary>
    /// Pick <paramref name="shape"/> and put the keyboard on it, as a screen reader selecting it asks: the
    /// same pick a click makes, which Delete and the menu then act on (§7.1).
    /// </summary>
    public void Pick(int? folder, MapShape shape)
    {
        if (IsOnScreen)
        {
            keyboard.Land(drawing, folder, shape, pick: true);
        }
    }

    /// <summary>Pick nothing, as a click on empty space does.</summary>
    public void Unpick()
    {
        if (IsOnScreen && Answers)
        {
            map.Unpick();
        }
    }

    /// <summary>Bring <paramref name="shape"/> into view, as a screen reader scrolling to it asks.</summary>
    public void BringIntoView(MapShape shape)
    {
        if (IsOnScreen)
        {
            map.Bring(drawing, shape);
        }
    }

    /// <summary>
    /// Open <paramref name="shape"/>, or zoom to it where it cannot be opened: what Enter and a
    /// double-click do. The map lands a change of folder still flying first, which can put another
    /// drawing on screen, so it is the map that asks whether this one still is.
    /// </summary>
    public void Open(int? folder, MapShape shape) => map.OpenShape(drawing, folder, shape);

    /// <summary>Whether the page describes the drawn node, without which none of its shapes is given.</summary>
    private bool Described => FactsOf(drawing.Root) is not null;

    private MapShapeFacts? FactsOf(int node) => map.Facts?.Invoke(drawing.Tree, node);
}
