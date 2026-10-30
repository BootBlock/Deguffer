namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// Where a view should put one piece of text, in canvas pixels.
///
/// <para>Positions rather than drawn text, because the labels are the one part of the picture that
/// is not in the bitmap: they are real controls laid over it, so they scale with the user's text
/// size and a screen reader can reach them. What belongs here is the part that is geometry, and
/// what belongs to the shell is the part that is a control.</para>
/// </summary>
/// <param name="X">The left edge of a box one line high. The text is trimmed to fit it.</param>
/// <param name="Y">The top of that box.</param>
/// <param name="Rotation">
/// How far to turn the text about the middle of its box, in degrees clockwise. Zero for anything
/// laid out in rectangles; a sunburst turns each label to lie along its own ring.
/// </param>
/// <param name="Centred">Whether the text sits in the middle of its box or starts at the left of it.</param>
/// <param name="Colour">
/// What colour the text has to be to stay legible against the shape underneath it. Decided here
/// because the surface is what knows the colour it painted that shape in.
/// </param>
/// <param name="Bytes">
/// What the shape accounts for. Carried for the blocks that are not nodes, whose figure a caption
/// cannot look up in the tree.
/// </param>
/// <param name="Room">
/// Where the text has to stay to be over its own shape and no other: the shape, or for a folder's
/// name the band set aside for it. What a picture magnified less than it was drawn at fades the
/// label against (see <see cref="Fade"/>).
/// </param>
public readonly record struct ExploreLabel(
    int Node,
    float X,
    float Y,
    float Width,
    float Rotation,
    bool Centred,
    TileColour Colour,
    long Bytes,
    LabelRoom Room)
{
    /// <summary>
    /// How much smaller than where it is gone a label is still whole: it fades over the last fifth of
    /// the way its shape shrinks before the text reaches the shape's edge. Enough to read as a fade at
    /// the speed of a zoom, and short enough that a name is never faint while its shape still has
    /// plenty of room for it.
    /// </summary>
    public const float FadeRange = 1.25f;

    /// <summary>
    /// The least magnification a picture at rest can be shown at. One only to within the canvas
    /// rounded to whole device pixels, which is half a pixel across it, and under a 256th of any
    /// canvas wider than 128 pixels. A label is whole down to this, so the rounding never dims one
    /// where its drawing put it, as it would a name that just fits, whose fade would otherwise run
    /// over less than that rounding.
    /// </summary>
    public const float AtRest = 1 - (1f / 256);

    /// <summary>
    /// The least a magnification can be for <see cref="Fade"/> to be measured against it. A text with no
    /// extent fits at any magnification, and a fade has to run between two different ones.
    /// </summary>
    private const float LeastGone = 1f / 1024;

    /// <summary>
    /// The point of the label that stays on its place in the picture as the picture is magnified,
    /// while the text keeps the reader's size: the start of the line, or its middle for a centred
    /// label, along the top of its box.
    /// </summary>
    public (float X, float Y) Anchor => (Centred ? X + (Width / 2) : X, Y);

    /// <summary>
    /// How opaque the label is as its picture is magnified, on each axis, for text
    /// <paramref name="textWidth"/> by <paramref name="textHeight"/> canvas pixels.
    ///
    /// <para>Text keeps its size while the shape it names grows and shrinks with the picture, so
    /// shrinking the picture brings the shape's edges in on it. Where the text reaches an edge it is
    /// gone, so it never lies over a shape that is not its own, and it fades out over the
    /// <see cref="FadeRange"/> before that. A shape that grows takes its name with it and never loses
    /// it.</para>
    ///
    /// <para>The text as measured rather than the box it was laid out in, because a short name in a
    /// wide shape stays readable long after the box would have run out of room.</para>
    /// </summary>
    public (LabelFade Across, LabelFade Down) Fade(float textWidth, float textHeight)
    {
        var (anchorX, anchorY) = Anchor;
        var before = Centred ? 0.5f : 0;

        return (
            AxisFade(anchorX - Room.Left, Room.Right - anchorX, textWidth * before, textWidth * (1 - before)),
            AxisFade(anchorY - Room.Top, Room.Bottom - anchorY, 0, textHeight));
    }

    /// <summary>
    /// The fade along one axis, for a text running <paramref name="textBefore"/> before the anchor and
    /// <paramref name="textAfter"/> after it, in a room reaching <paramref name="roomBefore"/> and
    /// <paramref name="roomAfter"/> from it.
    ///
    /// <para>Magnified by <c>m</c>, the room reaches <c>m</c> times as far and the text as far as it
    /// did, so a side is outgrown below the magnification that is their ratio.</para>
    /// </summary>
    private static LabelFade AxisFade(float roomBefore, float roomAfter, float textBefore, float textAfter)
    {
        var gone = Math.Max(Math.Max(Outgrown(textBefore, roomBefore), Outgrown(textAfter, roomAfter)), LeastGone);

        // Already past its room where it was drawn, or within the rounding of it. The layout chose to
        // name it, so it is whole there, as it was before the picture moved, and goes as soon as the
        // picture shrinks.
        return gone >= AtRest
            ? new LabelFade(AtRest / FadeRange, AtRest)
            : new LabelFade(gone, Math.Min(AtRest, gone * FadeRange));
    }

    private static float Outgrown(float text, float room) =>
        text <= 0 ? 0 : room <= 0 ? float.PositiveInfinity : text / room;
}
