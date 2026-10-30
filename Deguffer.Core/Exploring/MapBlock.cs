using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Exploring;

/// <summary>
/// A shape on the map that stands for no node: the items too small to draw one by one, or a part of
/// the volume that is not a file or a folder. What it is called, what it is, and what it is painted.
///
/// <para>One instance a block, chosen by <see cref="Of"/> from the node number the layout gave it
/// (see <see cref="ExploreTile"/>), so a new block is a new instance here rather than an edit to a
/// switch in each of the map's labels, its colours, the line under it and its screen reader text.</para>
///
/// <para>A block has one name, read alike on the map, to a screen reader and on the line under the
/// map on every page that draws one, so a reader with a pointer and a reader with a screen reader are
/// told the same thing of one shape. Where the line under the map says more, it says the name first
/// and adds to it, never words it another way.</para>
/// </summary>
public sealed class MapBlock
{
    private const string NotAFileOrAFolder =
        "Space on the drive that is not a file or a folder here, so it cannot be opened or removed.";

    /// <summary>
    /// The items too small to draw one by one, as one shape.
    ///
    /// <para>Its colour is outside the branch hues, and flat. It is not a thing on the disk, so a
    /// colour that reads as one would invite the reader to act on it.</para>
    /// </summary>
    public static readonly MapBlock Aggregate = new(
        ExploreTile.Aggregated,
        "Too small to draw one by one",
        readout: null,
        "Several items, each too small to draw here. The list view shows them one by one.",
        TileColour.FromRgb(0x707070));

    /// <summary>
    /// What the volume has left, drawn beside the whole of a scanned volume. A light neutral, apart from
    /// the aggregate's darker one and from every hue, because it is neither a thing on the disk nor a
    /// run of them.
    /// </summary>
    public static readonly MapBlock FreeSpace = new(
        ExploreTile.FreeSpace,
        "Free space",
        "Free space available on this drive",
        NotAFileOrAFolder,
        TileColour.FromRgb(0xC8C8C8));

    /// <summary>
    /// What the volume has in use that the scan did not count. The neutral between the aggregate's and
    /// the free space's, because it is in use like the one and not a thing on the disk like the other.
    /// </summary>
    public static readonly MapBlock Unaccounted = new(
        ExploreTile.Unaccounted,
        "Not accounted for",
        "Not accounted for by this scan, but in use",
        NotAFileOrAFolder,
        TileColour.FromRgb(0x9C9C9C));

    /// <summary>
    /// The space Windows states it has set aside for restore points and shadow copies. A neutral a step
    /// darker than the unaccounted block it was taken out of, so it reads as part of the same use and
    /// apart from it.
    /// </summary>
    public static readonly MapBlock ShadowCopies = new(
        ExploreTile.ShadowCopies,
        "Restore points and shadow copies",
        "Restore points and shadow copies, by Windows' own figure",
        NotAFileOrAFolder,
        TileColour.FromRgb(0x888888));

    /// <summary>
    /// The space Windows keeps back for updates. A neutral a step lighter than the unaccounted block,
    /// for the reason <see cref="ShadowCopies"/> is a step darker.
    /// </summary>
    public static readonly MapBlock ReservedStorage = new(
        ExploreTile.ReservedStorage,
        "Reserved storage",
        "Reserved storage, by Windows' own figure",
        NotAFileOrAFolder,
        TileColour.FromRgb(0xB0B0B0));

    private MapBlock(int node, string name, string? readout, string help, TileColour colour)
    {
        Node = node;
        Name = name;
        Readout = readout ?? name;
        Help = help;
        Colour = colour;
    }

    /// <summary>The node number a layout gives a shape of this block.</summary>
    public int Node { get; }

    /// <summary>What the block is called on the map and to a screen reader: "Free space".</summary>
    public string Name { get; }

    /// <summary>
    /// What the line under the map calls it: <see cref="Name"/>, and where that is not enough, more
    /// after it. "Free space available on this drive".
    /// </summary>
    public string Readout { get; }

    /// <summary>
    /// What the block is, and that nothing on the map acts on it (§7.1): the aggregate is many items,
    /// which Explore never acts on at once, and the rest is space that is not a file or a folder.
    /// </summary>
    public string Help { get; }

    /// <summary>
    /// What it is painted outside a high contrast theme. A neutral, never a branch hue, so a block
    /// belongs to no branch and is not mistaken for one.
    /// </summary>
    public TileColour Colour { get; }

    /// <summary>The block a shape of <paramref name="node"/> stands for, or null where it is a node of the tree.</summary>
    public static MapBlock? Of(int node) => node switch
    {
        ExploreTile.Aggregated => Aggregate,
        ExploreTile.FreeSpace => FreeSpace,
        ExploreTile.Unaccounted => Unaccounted,
        ExploreTile.ShadowCopies => ShadowCopies,
        ExploreTile.ReservedStorage => ReservedStorage,
        _ => null,
    };

    public override string ToString() => Name;
}
