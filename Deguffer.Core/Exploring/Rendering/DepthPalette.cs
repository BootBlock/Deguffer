namespace Deguffer.Core.Exploring.Rendering;

/// <summary>One entry of the depth legend: what it names, and what it is drawn in.</summary>
public readonly record struct DepthBand(string Label, TileColour Colour);

/// <summary>
/// What colour a shape is painted when the map is coloured by depth: one colour per level below the
/// folder on screen, the same for a file and a folder at that level, repeating every eight levels.
///
/// <para><b>SpaceMonger's eight colours</b>, in its order: red, orange, yellow, green, cyan, blue,
/// grey and magenta, as SpaceMonger 1.4 fills its boxes. Light enough that black text reads on every
/// one of them. Siblings share a colour, and some neighbouring levels are close in lightness, so it is
/// a line round each shape that tells them apart without colour, which is why a map coloured this way
/// wants one (<see cref="ShapeFinish"/>).</para>
///
/// <para><b>Colour is never the only carrier (§6.5).</b> The legend names every level, and the line
/// under the map says how many levels in the shape under the pointer is.</para>
///
/// <para><b>One set for every <see cref="Configuration.ExploreScheme"/>.</b> The meaning is in which
/// colour goes with which level, as with <see cref="TypePalette"/>.</para>
/// </summary>
public static class DepthPalette
{
    private static readonly TileColour[] Colours =
    [
        TileColour.FromRgb(0xFF7F7F),
        TileColour.FromRgb(0xFFBF7F),
        TileColour.FromRgb(0xFFFF00),
        TileColour.FromRgb(0x7FFF7F),
        TileColour.FromRgb(0x7FFFFF),
        TileColour.FromRgb(0xBFBFFF),
        TileColour.FromRgb(0xBFBFBF),
        TileColour.FromRgb(0xFF7FFF),
    ];

    /// <summary>Every level a legend lists, from the folder on screen inwards, before the colours repeat.</summary>
    private static readonly DepthBand[] Scale =
        [.. Colours.Select((colour, depth) => new DepthBand(Name(depth), colour))];

    /// <summary>Every band, in the order a legend lists them.</summary>
    public static IReadOnlyList<DepthBand> Bands => Scale;

    /// <summary>What a shape <paramref name="depth"/> levels below the folder on screen is painted.</summary>
    public static TileColour For(int depth) => Colours[(int)((uint)depth % (uint)Colours.Length)];

    /// <summary>
    /// How many levels below <paramref name="root"/> <paramref name="node"/> is in
    /// <paramref name="tree"/>, which is the depth a drawing of <paramref name="root"/> paints it at; or
    /// null where it is not under it.
    /// </summary>
    public static int? LevelOf(Layout.ISizedTree tree, int node, int root)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var depth = 0;

        for (var current = node; current != root; current = tree.ParentOf(current), depth++)
        {
            // The tree's own root is its own parent, so the walk has run out above the root asked for.
            if (tree.ParentOf(current) == current)
            {
                return null;
            }
        }

        return depth;
    }

    /// <summary>
    /// How a shape <paramref name="depth"/> levels below the folder on screen is named: the folder
    /// itself, or how many levels in it is.
    /// </summary>
    public static string Name(int depth) => depth switch
    {
        <= 0 => "The folder shown",
        1 => "1 level in",
        _ => $"{depth} levels in",
    };
}
