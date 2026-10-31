using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// What a double-click, or Enter on the shape the keyboard is on, does with a shape: ask the page to
/// open it, and where the page does not, zoom to it.
///
/// <para>The page is what decides whether a node opens, and it says so by showing another picture. What
/// does not open, a file, the block standing in for items too small to draw, or the block standing for
/// free space, is zoomed to until it fills the map as far as its shape allows. For the block of small
/// items that is how they come to be drawn one by one.</para>
///
/// <para>In Core rather than in the shell, as <see cref="MapPress"/> is, so the rules are provable
/// without a window (G8).</para>
/// </summary>
public static class MapOpen
{
    /// <summary>
    /// Whether opening <paramref name="hit"/> asks the page to open it, in a drawing of
    /// <paramref name="drawn"/> that has the volume beside it or not.
    ///
    /// <para>Only a node opens. The node the whole drawing is of opens only out of what is drawn beside
    /// it: the volume's free space, beside the root of a treemap of a whole drive. Anywhere else it is
    /// already open, and asking the page to open it again would be asking for the picture on screen.</para>
    /// </summary>
    /// <param name="drawn">The node the page showed, which the whole drawing is of.</param>
    public static bool AsksPage(ExploreHit hit, int drawn, bool volumeBeside) =>
        hit.IsNode && (hit.Node != drawn || volumeBeside);

    /// <summary>
    /// Where the camera goes to show a shape that did not open, at <paramref name="shape"/> on a screen
    /// showing <paramref name="shown"/>, as far in as <paramref name="ceiling"/> allows; or null where it
    /// stays, because the map does not zoom or there is no shape to zoom to.
    /// </summary>
    /// <param name="shape">
    /// Where the shape is on the screen, in fractions of it, or null where it has no rectangle to fit:
    /// only a treemap nests, and an icicle draws what a shape holds below it rather than in it.
    /// </param>
    public static MapViewport? ZoomTo(bool zoomable, MapFrame? shape, MapViewport shown, double ceiling) =>
        zoomable && shape is { } frame ? MapViewport.Fitting(shown.PictureOf(frame), ceiling) : null;
}
