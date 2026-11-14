using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Drawings of a scanned tree coloured by one of the colourings, as a test asks for them. The page
/// hands <see cref="ExploreSurface.Create"/> colours it has already made, so only a test names a
/// colouring there.
/// </summary>
internal static class Surfaces
{
    /// <summary>
    /// <paramref name="root"/> of <paramref name="tree"/> laid out for <paramref name="view"/>, coloured
    /// to say <paramref name="colouring"/> in <paramref name="scheme"/>, with <paramref name="nowUtc"/>
    /// as "now" for the age bands.
    /// </summary>
    public static ExploreSurface Create(
        ExploreTree tree,
        int root,
        ExploreView view,
        int width,
        int height,
        double scale,
        double textScale,
        ExploreColouring colouring,
        ExploreScheme scheme,
        DateTime nowUtc,
        ExploreSpacing spacing,
        VolumeSpace volume) =>
        ExploreSurface.Create(
            tree, root, view, width, height, scale, textScale,
            ShapeColours.For(tree, colouring, scheme, nowUtc, growth: null, types: null),
            MapLook.Default with { Spacing = spacing }, volume);
}
