using System.Collections.Frozen;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// The shades SpaceMonger 1.4 draws the edges of its eight colours in (<see cref="Configuration.MapBevelShades.Classic"/>):
/// a lighter one along the top and the left of a box and a darker one along the bottom and the right.
///
/// <para>A table rather than a mix, because SpaceMonger's are not one mix of every colour. They were
/// read from its own pixels: green's lighter shade is half way to white, cyan's three quarters of the
/// way, and lavender's darker shade keeps its blue whole where every other colour's loses a quarter.
/// Any colour outside the eight is mixed as the relief says.</para>
/// </summary>
public static class ClassicBevel
{
    private static readonly FrozenDictionary<TileColour, (TileColour Lit, TileColour Shaded)> Shades = new Dictionary<TileColour, (TileColour Lit, TileColour Shaded)>
    {
        [TileColour.FromRgb(0xFF7F7F)] = (TileColour.FromRgb(0xFFBFBF), TileColour.FromRgb(0xBF7F7F)),
        [TileColour.FromRgb(0xFFBF7F)] = (TileColour.FromRgb(0xFFDFBF), TileColour.FromRgb(0xBF9F5F)),
        [TileColour.FromRgb(0xFFFF00)] = (TileColour.FromRgb(0xFFFFBF), TileColour.FromRgb(0xBFBF3F)),
        [TileColour.FromRgb(0x7FFF7F)] = (TileColour.FromRgb(0xBFFFBF), TileColour.FromRgb(0x7FBF7F)),
        [TileColour.FromRgb(0x7FFFFF)] = (TileColour.FromRgb(0xDFFFFF), TileColour.FromRgb(0x7FBFBF)),
        [TileColour.FromRgb(0xBFBFFF)] = (TileColour.FromRgb(0xDFDFFF), TileColour.FromRgb(0x9F9FFF)),
        [TileColour.FromRgb(0xBFBFBF)] = (TileColour.FromRgb(0xDFDFDF), TileColour.FromRgb(0x9F9F9F)),
        [TileColour.FromRgb(0xFF7FFF)] = (TileColour.FromRgb(0xFFBFFF), TileColour.FromRgb(0xBF7FBF)),
    }.ToFrozenDictionary();

    /// <summary>SpaceMonger's two shades of <paramref name="fill"/>, where it is one of its eight colours.</summary>
    public static bool TryShade(TileColour fill, out TileColour lit, out TileColour shaded)
    {
        if (Shades.TryGetValue(fill, out var shades))
        {
            (lit, shaded) = shades;
            return true;
        }

        (lit, shaded) = (default, default);
        return false;
    }
}
