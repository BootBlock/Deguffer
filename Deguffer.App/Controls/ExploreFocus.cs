using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// Which shape on the map the keyboard is on: the folder whose shapes it moves between, the shape, and
/// where the shape stood in the picture, so it can be found again in the next drawing of it.
///
/// <para>Separate from <see cref="ExploreKeyboard"/>, which moves the keyboard and says what follows
/// from it. This only remembers where it is and finds it again: by the node, which every drawing of the
/// same tree names alike, or where the drawing no longer draws that shape on its own (a block of the
/// rest, once a zoom has drawn what it stood for one by one), by the shape nearest where it stood
/// (<see cref="MapNeighbours.Nearest"/>).</para>
/// </summary>
internal sealed class ExploreFocus
{
    /// <summary>Where the shape stood, in fractions of the whole picture, which every zoom of it agrees on.</summary>
    private (double X, double Y) _at = (0.5, 0.5);

    /// <summary>The folder whose shapes the keyboard moves between, or null for the top of the picture.</summary>
    public int? Folder { get; private set; }

    /// <summary>The node or block the keyboard is on, or null where it has not been put on one.</summary>
    public int? Node { get; private set; }

    /// <summary>Forget the shape, so the next drawing starts the keyboard on its first, at the top of the picture.</summary>
    public void Clear()
    {
        Folder = null;
        Node = null;
        _at = (0.5, 0.5);
    }

    /// <summary>
    /// Read the shape and its folder in a new tree of the same place, through <paramref name="counterpart"/>.
    /// A folder the new tree no longer has takes the keyboard back to the top, and a block stays the
    /// block it was.
    /// </summary>
    public void Carry(Func<int, int?> counterpart)
    {
        Folder = Folder is { } folder ? counterpart(folder) : null;
        Node = Node is { } node and >= 0 ? counterpart(node) : Node;
    }

    /// <summary>Put the keyboard on <paramref name="node"/> among the shapes of <paramref name="folder"/>, to be found in the next drawing.</summary>
    public void Expect(int? folder, int node)
    {
        Folder = folder;
        Node = node;
    }

    /// <summary>
    /// Put the keyboard on <paramref name="shape"/> among the shapes of <paramref name="folder"/>, in
    /// <paramref name="drawing"/>.
    /// </summary>
    public void Set(ExploreSurface drawing, int? folder, MapShape shape)
    {
        Folder = folder;
        Node = shape.Node;
        _at = (drawing.Viewport ?? MapViewport.Whole).PictureAt(shape.X, shape.Y);
    }

    /// <summary>
    /// Put the keyboard down in <paramref name="drawing"/>, and give the shapes of its folder there and
    /// which of them it is on: the same node, or the shape nearest where it stood, or the first where it
    /// was on none. At the top of the picture where the folder is no longer drawn. Null where the drawing
    /// has no shapes at all.
    ///
    /// <para>A command rather than a query: where the drawing no longer draws the shape, the keyboard is
    /// on the one found from now on, so every reader of <see cref="Node"/> agrees with what is marked.</para>
    /// </summary>
    public (IReadOnlyList<MapShape> Shapes, int Index)? Land(ExploreSurface drawing)
    {
        var shapes = drawing.Shapes(Folder);

        if (shapes.Count == 0 && Folder is not null)
        {
            Folder = null;
            shapes = drawing.Shapes(null);
        }

        if (shapes.Count == 0)
        {
            return null;
        }

        var index = Node is { } node ? MapShapes.IndexOf(shapes, node) ?? Nearest(drawing, shapes) : 0;

        Set(drawing, Folder, shapes[index]);

        return (shapes, index);
    }

    private int Nearest(ExploreSurface drawing, IReadOnlyList<MapShape> shapes)
    {
        var (x, y) = (drawing.Viewport ?? MapViewport.Whole).ScreenAt(_at.X, _at.Y);

        return MapNeighbours.Nearest(shapes, x, y, drawing.Width, drawing.Height);
    }
}
