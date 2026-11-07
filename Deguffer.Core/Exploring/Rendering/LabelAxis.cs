namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How a label finds its place along one axis, as a rule rather than a position, so the place can be
/// worked out again for the picture at any magnification. See <see cref="ExploreLabel.Across"/>.
///
/// <para>A rule because a label's place is not a point in the picture. Its padding and the room a
/// band leaves round a line of text stay the same number of pixels on the screen while the picture
/// grows, and a shape running off the screen keeps its name on the screen rather than taking it
/// off with it. A label carried by its point alone through a zoom is somewhere else when the drawing
/// at the new zoom arrives, and it jumps there. Worked out from this rule on every frame of the zoom,
/// it is already where that drawing puts it.</para>
/// </summary>
/// <param name="From">Where the span the label is placed along starts in the picture, in canvas pixels.</param>
/// <param name="To">Where that span ends in the picture, in canvas pixels.</param>
/// <param name="Inset">
/// How far from the start of the span, or from its middle where <paramref name="Centred"/>, the
/// label's anchor is, in canvas pixels of the screen: it does not grow with the picture.
/// </param>
/// <param name="Centred">Whether the anchor is measured from the middle of the span rather than its start.</param>
/// <param name="KeptOnCanvas">
/// Whether the span is cut to the part of it on the canvas first, so a shape running off an edge
/// keeps its name at that edge.
/// </param>
public readonly record struct LabelAxis(float From, float To, float Inset, bool Centred, bool KeptOnCanvas)
{
    /// <summary>An anchor at <paramref name="at"/> in the picture, carried with it wherever it goes.</summary>
    public static LabelAxis Fixed(float at) => new(at, at, 0, Centred: false, KeptOnCanvas: false);

    /// <summary>
    /// The anchor along this axis on a canvas <paramref name="extent"/> pixels long, where the span lies
    /// from <paramref name="from"/> to <paramref name="to"/>: <see cref="From"/> and <see cref="To"/>
    /// as the picture is magnified and moved. The map's compositor works the same sum out on every
    /// frame (see <c>ExploreLabels</c>).
    /// </summary>
    public float At(float from, float to, float extent)
    {
        if (KeptOnCanvas)
        {
            from = Math.Max(from, 0);
            to = Math.Min(to, extent);
        }

        return (Centred ? (from + to) / 2 : from) + Inset;
    }
}
