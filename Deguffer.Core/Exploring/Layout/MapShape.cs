namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// One shape a drawing of the map draws in a folder, as the keyboard and a screen reader reach it:
/// what it stands for, how much, and where it is (<see cref="Rendering.ExploreSurface.Shapes"/>).
/// </summary>
/// <param name="Node">The node, or one of the blocks of <see cref="ExploreTile"/> that stand for no node.</param>
/// <param name="Bytes">How much it stands for. Carried for the blocks, which have no node to look it up from.</param>
/// <param name="Frame">
/// The box round it, in fractions of the drawing's canvas, on the canvas or however far off it: what a
/// screen reader outlines, and what the camera brings into view.
/// </param>
/// <param name="X">
/// Across, in the same fractions, the point that stands for where the shape is: its middle, or for a
/// wedge the middle of its arc, which the middle of its box may be outside it.
/// </param>
/// <param name="Y">Down, in the same fractions, the same point.</param>
public readonly record struct MapShape(int Node, long Bytes, MapFrame Frame, double X, double Y)
{
    /// <summary>Whether the shape is a node of the tree, and so something that can be picked (§7.1).</summary>
    public bool IsNode => Node >= 0;

    /// <summary>A rectangle, which stands where its middle is.</summary>
    public static MapShape Of(int node, long bytes, MapFrame frame) =>
        new(node, bytes, frame, frame.Centre.X, frame.Centre.Y);
}
