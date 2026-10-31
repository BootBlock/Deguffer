namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How far a map's picture can be magnified: as far as a deeper zoom still has detail to show.
///
/// <para>A drawing knows what in view it could not draw one by one: the block standing in for items
/// too small to draw, and a folder too small to frame (<see cref="ExploreTile.Finest"/>). While there
/// is any, a deeper zoom reveals new shapes, so the ceiling is above the zoom on screen, as deep as
/// the smallest of the items directly in them needs to be drawn (see
/// <see cref="Rendering.DrawnZoom.Ceiling"/>). A folder among those items opens there, and the
/// drawing made there measures what it holds.
/// Once everything in view is drawn, the ceiling is where the smallest shape in view has room for its
/// name, which is where the zoom is once every shape has: past that a deeper zoom only magnifies
/// shapes already drawn and named. Each drawing sets it afresh, so it rises as the reader zooms into
/// detail and stops where the detail ends.</para>
/// </summary>
public static class MapCeiling
{
    /// <summary>
    /// The ceiling at its lowest, so a small tree drawn whole still zooms. Sixty-four times gives a
    /// shape three pixels across room for a name on any screen.
    /// </summary>
    public const double Least = 64;

    /// <summary>
    /// The ceiling at its highest: the precision limit. The camera and every placement are measured
    /// from an origin near what is shown (<see cref="MapOrigin"/>), so the compositor's single
    /// precision does not limit the zoom; the double-precision fractions a viewport is held in do. At
    /// this zoom a step in the last place of a fraction is under a thousandth of a pixel on an 8K screen.
    /// A single byte of a volume of hundreds of terabytes is drawn well before it.
    /// </summary>
    public const double Most = 1 << 30;

    /// <summary>
    /// The least a ceiling rises above the zoom while there is detail in view to draw: one more level
    /// of detail, which a treemap lays out at each doubling of the zoom (see <see cref="TreemapDetail"/>).
    /// What decides whether a shape is drawn is that level's, and the estimate in <see cref="Revealing"/>
    /// is of its size alone, so this is what makes sure each drawing lets the zoom go somewhere new.
    /// </summary>
    public const double Step = 2;

    /// <summary>
    /// How many times more a picture has to be magnified for an item <paramref name="finest"/> pixels
    /// across to be drawn by a layout whose smallest shape is <paramref name="smallest"/> pixels across,
    /// and never less than <see cref="Step"/>. Until it is twice that: the finest level of detail at any
    /// zoom draws shapes down to between one and two of the smallest across, so twice is drawn at every
    /// zoom.
    /// </summary>
    public static double Revealing(double finest, double smallest) => Math.Max(Step, 2 * smallest / finest);

    /// <summary>
    /// How many times more a picture has to be magnified for a shape <paramref name="width"/> by
    /// <paramref name="height"/> pixels to have room for a name <paramref name="labelWidth"/> by
    /// <paramref name="labelHeight"/> pixels, and a pixel more each way: at most 1 where it already has.
    ///
    /// <para>The pixel is what makes the room certain. A zoom to exactly the label's size leaves the
    /// shapes at the threshold by a rounding error either side of it, so some are named and their equal
    /// neighbours are not, and the next drawing asks for a rounding error more.</para>
    /// </summary>
    public static double Naming(double width, double height, double labelWidth, double labelHeight) =>
        Math.Max((labelWidth + 1) / width, (labelHeight + 1) / height);

    /// <summary>
    /// The ceiling for a drawing made at <paramref name="zoom"/> whose detail in view is all drawn and
    /// named once magnified <paramref name="deeper"/> times more, 1 where it already is: never below the
    /// zoom, nor below <see cref="Least"/>, nor above <see cref="Most"/>.
    /// </summary>
    public static double Of(double zoom, double deeper) => Math.Clamp(zoom * deeper, Least, Most);

    /// <summary>
    /// The ceiling once a drawing allowing a zoom up to <paramref name="drawn"/> arrives, with the
    /// camera going to <paramref name="target"/>, showing <paramref name="shown"/>, and held to
    /// <paramref name="ceiling"/> until now. Never above <see cref="Most"/>.
    ///
    /// <para>Never below where the camera is going, nor below what it shows within the ceiling it had:
    /// a drawing can arrive while the camera is on its way somewhere deeper than the drawing was made
    /// at, and a lower ceiling stops the next zoom, never one already made. The drawing made where the
    /// camera stops sets it again.</para>
    /// </summary>
    public static double Drawn(double drawn, double ceiling, MapViewport target, MapViewport shown) =>
        Math.Min(Math.Max(drawn, Math.Max(target.Zoom, Math.Min(shown.Zoom, ceiling))), Most);

    /// <summary>
    /// The ceiling once the camera is asked to <paramref name="target"/> under
    /// <paramref name="ceiling"/>: raised to the target's zoom where that is deeper, and never above
    /// <see cref="Most"/>. Every target the map works out itself is held to the ceiling, so a deeper
    /// one is a place the reader was, which had detail to show when they were there.
    /// </summary>
    public static double Toward(double ceiling, MapViewport target) => Math.Min(Math.Max(ceiling, target.Zoom), Most);

    /// <summary>
    /// The ceiling for a map handed something else to draw, opened at <paramref name="viewport"/>: the
    /// old ceiling belonged to the drawing of what the map showed before, so it goes back to
    /// <see cref="Least"/>, or to the zoom the reader was at, until the new picture's first drawing
    /// sets it. Never above <see cref="Most"/>.
    /// </summary>
    public static double Afresh(MapViewport viewport) => Math.Min(Math.Max(Least, viewport.Zoom), Most);
}
