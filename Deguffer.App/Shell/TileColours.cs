using Deguffer.Core.Exploring.Rendering;
using Windows.UI;

namespace Deguffer.App.Shell;

/// <summary>How a map's colour and the colour Windows draws with stand for each other.</summary>
internal static class TileColours
{
    /// <summary><paramref name="colour"/>, opaque, as every colour the map draws is.</summary>
    public static Color ToColor(this TileColour colour) => Color.FromArgb(255, colour.Red, colour.Green, colour.Blue);

    /// <summary>
    /// <paramref name="colour"/>'s red, green and blue. Its alpha is not kept: a map's colour is
    /// always opaque, and the colours read this way are a theme's, which are too.
    /// </summary>
    public static TileColour ToTile(this Color colour) => new(colour.R, colour.G, colour.B);
}
