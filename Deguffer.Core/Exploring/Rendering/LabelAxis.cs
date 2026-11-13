namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How a label finds its place along one axis, as a rule rather than a position, so the place can be
/// worked out again for the picture at any magnification. See <see cref="ExploreLabel.Across"/>.
///
/// <para>A rule because a label's place is not a point in the picture. Its padding and the room a
/// band leaves round a line of text stay the same number of pixels on the screen while the picture
/// grows, and a shape running off the screen keeps its name on the screen rather than taking it
/// off with it. While the picture moves, a name keeps the distance from <see cref="Carried"/> that
/// the drawing put it at, so every name of a drawing moves by one transform; the drawing made where
/// the picture stops works the rule out again.</para>
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
    /// The point of the picture the anchor moves with while the picture moves: the start of the span,
    /// or its middle where <see cref="Centred"/>, before it is cut to the canvas or inset.
    ///
    /// <para>A moving picture carries each name by this point, at the distance from it
    /// <see cref="Drawn"/> put the name, so a whole drawing's names move with one transform. A name of
    /// a shape running off the canvas goes with the shape while the picture moves, and the drawing made
    /// where it stops puts it back at the edge.</para>
    /// </summary>
    public float Carried => Centred ? (From + To) / 2 : From;

    /// <summary>The anchor along this axis on the canvas the label was laid out for, <paramref name="extent"/> pixels long.</summary>
    public float Drawn(float extent) => At(From, To, extent);

    /// <summary>
    /// The anchor along this axis on a canvas <paramref name="extent"/> pixels long, where the span lies
    /// from <paramref name="from"/> to <paramref name="to"/>: <see cref="From"/> and <see cref="To"/>
    /// as the picture is magnified and moved.
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
