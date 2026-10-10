namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One shape's move between two layouts of the same place, in fractions of the drawing (as
/// <see cref="Rendering.ExploreSurface.FrameOf"/> measures): from where it was to where it is.
/// </summary>
/// <param name="Node">
/// The shape's node in the later layout's tree, or, for one that
/// <see cref="ShapeChangeKind.Disappeared"/>, in the earlier one's. A negative node is one of the
/// shapes that stand for no node, such as the volume's free space, and is the same in both.
/// </param>
/// <param name="From">
/// Where it was. Of no size, at the middle of <paramref name="To"/>, for a shape that appeared.
/// </param>
/// <param name="To">
/// Where it is. Of no size, at the middle of <paramref name="From"/>, for a shape that disappeared.
/// </param>
public readonly record struct ShapeChange(ShapeChangeKind Kind, int Node, MapFrame From, MapFrame To);
