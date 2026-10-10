namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Where a shape is drawn at the start and the end of the map's move from one layout to the next, as
/// a sprite showing its own rectangle of one drawing (<see cref="ShapeChanges"/>).
///
/// <para>A shape that moved or appeared shows the later drawing, shaded already at its final size,
/// and is scaled from where it was to where it is, so nothing is drawn again during the move. A shape
/// that went shows the earlier drawing, shrinking to nothing at its middle. Scaled about the sprite's
/// top-left corner, in pixels of the canvas both drawings share.</para>
/// </summary>
/// <param name="ShowsEarlier">Whether the sprite shows the earlier drawing rather than the later one.</param>
/// <param name="Source">The rectangle of that drawing the sprite shows, in its pixels: the sprite's size, and where it reads from.</param>
/// <param name="Start">Where the sprite is as the move starts.</param>
/// <param name="End">Where it is as the move ends.</param>
public readonly record struct ShapeTravel(bool ShowsEarlier, MapFrame Source, SpritePlacement Start, SpritePlacement End)
{
    /// <summary>
    /// The travel of <paramref name="change"/> on a canvas of <paramref name="width"/> by
    /// <paramref name="height"/> pixels, or null for a shape too thin to show at its end.
    /// </summary>
    public static ShapeTravel? Of(ShapeChange change, int width, int height)
    {
        var from = Pixels(change.From, width, height);
        var to = Pixels(change.To, width, height);

        if (change.Kind == ShapeChangeKind.Disappeared)
        {
            return from.Width > 0 && from.Height > 0
                ? new ShapeTravel(true, from, new SpritePlacement(from.X, from.Y, 1, 1), new SpritePlacement(to.X, to.Y, 0, 0))
                : null;
        }

        if (to.Width <= 0 || to.Height <= 0)
        {
            return null;
        }

        return new ShapeTravel(
            false,
            to,
            new SpritePlacement(from.X, from.Y, from.Width / to.Width, from.Height / to.Height),
            new SpritePlacement(to.X, to.Y, 1, 1));
    }

    private static MapFrame Pixels(MapFrame frame, int width, int height) =>
        new(frame.X * width, frame.Y * height, frame.Width * width, frame.Height * height);
}
