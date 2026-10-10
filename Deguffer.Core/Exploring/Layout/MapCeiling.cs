namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How far a map's picture can be magnified: as far as a deeper zoom still has detail to show.
///
/// <para>A drawing knows what in view it could not draw one by one: the block standing in for items
/// too small to draw, and a folder too small to frame (<see cref="ExploreTile.Finest"/>). While there
/// is any, a deeper zoom reveals new shapes, so the ceiling is above the zoom on screen, as deep as
/// the smallest of those items needs to be drawn (see <see cref="Rendering.ExploreSurface.Ceiling"/>).
/// Once everything in view is drawn, a deeper zoom only magnifies shapes already there, so the ceiling
/// is where the zoom is. Each drawing sets it afresh, so it rises as the reader zooms into detail and
/// stops where the detail ends.</para>
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
    /// The ceiling for a drawing made at <paramref name="zoom"/> whose undrawn detail in view is drawn
    /// once magnified <paramref name="deeper"/> times more, 1 where it drew everything in view: never
    /// below the zoom, nor below <see cref="Least"/>, nor above <see cref="Most"/>.
    /// </summary>
    public static double Of(double zoom, double deeper) => Math.Clamp(zoom * deeper, Least, Most);
}
