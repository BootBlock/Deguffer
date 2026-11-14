using System.Text.Json.Serialization;

namespace Deguffer.Core.Configuration;

/// <summary>
/// How much of a tree every map draws and how its names read: the smallest shape it draws, how many
/// levels of folders it opens, how large its names are set, how many it writes and whether each is
/// followed by its size.
///
/// <para>Apart from <see cref="MapStyle"/>, as <see cref="MapPointerLight"/> is, because it is how
/// much the reader wants to see rather than how the shapes are finished. A preset sets the finish as a
/// whole, and a reader who asked for larger names has not asked for them back by choosing one.</para>
///
/// <para>Presentation only: a shape too small to draw is still in the tree and still in every list,
/// and nothing here changes what may be removed. A group of its own in the preferences, so a file
/// written before it existed reads with the detail the map always had.</para>
/// </summary>
/// <param name="SmallestShape">The smallest shape the map draws on its own, rather than in the block standing for the rest.</param>
/// <param name="Levels">How many levels of folders the treemap and the sunburst open.</param>
/// <param name="TextSize">How large the application's own lettering is set, on top of the reader's Windows text size.</param>
/// <param name="Names">How many shapes the map names at most.</param>
/// <param name="Sizes">Whether a name in the application's lettering is followed by its size, and SpaceMonger's has a line of bytes.</param>
public sealed record MapDetail(
    MapSmallestShape SmallestShape = MapSmallestShape.Standard,
    MapLevels Levels = MapLevels.Standard,
    MapTextSize TextSize = MapTextSize.Standard,
    MapNameCount Names = MapNameCount.Standard,
    bool Sizes = true)
{
    /// <summary>The detail a map has until the reader chooses another.</summary>
    public static readonly MapDetail Default = new();

    /// <summary>
    /// The smallest shape drawn, in device-independent pixels across. Three is QDirStat's, which the
    /// map has always drawn with (<see cref="Exploring.Layout.LayoutLimits.MinimumTileSize"/>).
    /// </summary>
    [JsonIgnore]
    public float SmallestShapeInDips => SmallestShape switch
    {
        MapSmallestShape.Large => 6f,
        MapSmallestShape.Small => 2f,
        MapSmallestShape.Tiny => 1f,
        _ => 3f,
    };

    /// <summary>
    /// How many levels the treemap and the sunburst descend. Six is WinDirStat's, which the map has
    /// always drawn with (<see cref="Exploring.Layout.LayoutLimits.MaximumDepth"/>).
    /// </summary>
    [JsonIgnore]
    public int LevelCount => Levels switch
    {
        MapLevels.Fewer => 4,
        MapLevels.More => 8,
        MapLevels.Most => 10,
        _ => 6,
    };

    /// <summary>
    /// The most shapes named in the application's lettering. Sixty-four is the number the map has
    /// always named: past a few dozen, names are noise over the picture, and some readers want that
    /// noise. SpaceMonger's lettering names every box with room for a name, whatever this says.
    /// </summary>
    [JsonIgnore]
    public int MostNames => Names switch
    {
        MapNameCount.Fewer => 24,
        MapNameCount.More => 160,
        MapNameCount.Most => 400,
        _ => 64,
    };

    /// <summary>
    /// The text size a map's names are set at in <paramref name="lettering"/>, for a reader whose
    /// Windows text size is <paramref name="system"/>: the two multiplied for the application's own
    /// lettering. SpaceMonger's is a pixel type, drawn unsmoothed at the one size it was made at, so
    /// it follows Windows alone, as it always has.
    /// </summary>
    public double TextScaleFor(MapLettering lettering, double system) =>
        lettering == MapLettering.Classic ? system : system * TextSize switch
        {
            MapTextSize.Small => 0.85,
            MapTextSize.Large => 1.2,
            MapTextSize.Larger => 1.4,
            _ => 1,
        };

    /// <summary>
    /// This detail with every value no member names read as the default's. The preferences file is
    /// read with numbers allowed for an enum, as <see cref="MapStyle.Defined"/> says.
    /// </summary>
    public MapDetail Defined() => new(
        StoredEnum.Defined(SmallestShape, Default.SmallestShape),
        StoredEnum.Defined(Levels, Default.Levels),
        StoredEnum.Defined(TextSize, Default.TextSize),
        StoredEnum.Defined(Names, Default.Names),
        Sizes);
}

/// <summary>
/// The smallest shape a map draws on its own. Ordinal from the coarsest to the finest, and the
/// appearance window lists them in this order. Stored by name, as <see cref="MapShading"/> is.
/// </summary>
public enum MapSmallestShape
{
    /// <summary>Six pixels across at 100%: fewer, larger shapes, each easy to point at.</summary>
    Large = 0,

    /// <summary>Three pixels across at 100%, as the map has always drawn.</summary>
    Standard = 1,

    /// <summary>Two pixels across at 100%.</summary>
    Small = 2,

    /// <summary>One pixel at 100%: every shape a pixel can show.</summary>
    Tiny = 3,
}

/// <summary>How many levels of folders a map opens. Ordinal, as the others are.</summary>
public enum MapLevels
{
    /// <summary>Four levels.</summary>
    Fewer = 0,

    /// <summary>Six levels, as the map has always drawn.</summary>
    Standard = 1,

    /// <summary>Eight levels.</summary>
    More = 2,

    /// <summary>Ten levels.</summary>
    Most = 3,
}

/// <summary>How large the application's lettering is set on a map. Ordinal, as the others are.</summary>
public enum MapTextSize
{
    Small = 0,
    Standard = 1,
    Large = 2,
    Larger = 3,
}

/// <summary>How many shapes a map names at most. Ordinal, as the others are.</summary>
public enum MapNameCount
{
    Fewer = 0,
    Standard = 1,
    More = 2,
    Most = 3,
}
