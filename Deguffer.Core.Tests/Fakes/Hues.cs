using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Stand-in branch colours for tests about where pixels go rather than what a colour means. Each
/// branch number is its own arc of the circle, and the depth is taken one level down so that
/// depth zero is a hue rather than the neutral grey the palette keeps for a drawing's root.
/// </summary>
internal static class Hues
{
    public static BranchHue Of(int branch) => new((branch * 45) % 360, 30, Lifted: false);

    public static TileColour Colour(int branch, int depth) => TilePalette.For(Of(branch), depth + 1, ExploreScheme.Standard);
}
