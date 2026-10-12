using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the page that owns a tree knows of one of its nodes and the map does not: what it is called,
/// what kind of thing it is, and whether opening it shows what it holds.
///
/// <para>Not whether it may be removed, which <see cref="MapShapeText.Help"/> is given apart. A screen
/// reader names every shape in a search of the window, and the verdict asks the machine where the
/// item's volume is mounted, so a verdict carried here would be asked once a shape.</para>
/// </summary>
/// <param name="Name">Its own name, not its path: the shapes round it are its siblings, and a screen reader reads the name first.</param>
/// <param name="Kind">What it is, as one capitalised noun or two: "Folder", "File", "Program".</param>
/// <param name="Opens">Whether opening it shows what it holds, as a double-click on its shape does.</param>
public sealed record MapShapeFacts(string Name, string Kind, bool Opens);

/// <summary>
/// What a screen reader says of one shape on the map: its name, how much it is and how much of what
/// holds it, then what it is and what can be done with it.
///
/// <para>The same answers a reader with a pointer gets from the line under the map and from the menu,
/// in words, because the picture says them in sizes and colours a screen reader cannot read. Decided
/// here rather than in the shell, so the share and the wording are proved without a window (G1).</para>
/// </summary>
public static class MapShapeText
{
    /// <summary>
    /// The shape's name as a screen reader reads it: "Documents, 12.3 GB, 45% of Users". The share is
    /// left out where the folder holding it has no size to be a share of.
    /// </summary>
    /// <param name="ofName">
    /// What holds it: the folder's name, or null for the drive, beside which a scan of a whole volume
    /// draws its root and the space that is not in it.
    /// </param>
    public static string Name(string name, long bytes, long ofBytes, string? ofName)
    {
        ArgumentNullException.ThrowIfNull(name);

        var size = $"{name}, {FreeSpace.Format(bytes)}";

        return ofBytes > 0 ? $"{size}, {Share(bytes, ofBytes)} of {ofName ?? "the drive"}" : size;
    }

    /// <summary>
    /// What the shape is and what can be done with it from the keyboard: "Folder. Enter opens it.
    /// Delete sends it to the Recycle Bin, after asking." A shape that may not be removed says why, in
    /// the words the selection's note uses, so the reason is heard before Delete is pressed.
    /// </summary>
    /// <param name="removal">
    /// Whether it may be removed, and if not why; or null where the page removes nothing, so nothing is
    /// said either way. The verdict the removal itself asks again before anything is deleted (§7.1).
    /// </param>
    public static string Help(MapShapeFacts facts, ExploreVerdict? removal)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var help = $"{facts.Kind}.";

        if (facts.Opens)
        {
            help += " Enter opens it.";
        }

        return removal switch
        {
            null => help,
            { IsAllowed: true } => $"{help} Delete sends it to the Recycle Bin, after asking.",
            { } refused => $"{help} It cannot be removed. {refused.Reason}",
        };
    }

    /// <summary>
    /// What a block that stands for no node is called, on the map and to a screen reader alike: see
    /// <see cref="ExploreTile"/>.
    /// </summary>
    public static string BlockName(int block) => block switch
    {
        ExploreTile.Aggregated => "Too small to draw one by one",
        ExploreTile.FreeSpace => "Free space",
        ExploreTile.Unaccounted => "Not accounted for",
        ExploreTile.ShadowCopies => "Restore points and shadow copies",
        ExploreTile.ReservedStorage => "Reserved storage",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Not a block: a node has a name of its own."),
    };

    /// <summary>
    /// What a block is, and that nothing on the map acts on it (§7.1): the aggregate is many items, which
    /// Explore never acts on at once, and the rest is space that is not a file or a folder.
    /// </summary>
    public static string BlockHelp(int block) => block switch
    {
        ExploreTile.Aggregated => "Several items, each too small to draw here. The list view shows them one by one.",
        ExploreTile.FreeSpace or ExploreTile.Unaccounted or ExploreTile.ShadowCopies or ExploreTile.ReservedStorage =>
            "Space on the drive that is not a file or a folder here, so it cannot be opened or removed.",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Not a block: a node has facts of its own."),
    };

    /// <summary>
    /// <paramref name="bytes"/> as a share of <paramref name="ofBytes"/>, to the nearest percent:
    /// "under 1%" rather than "0%" for a shape that is there, and "all" for one that is the whole of it.
    /// </summary>
    public static string Share(long bytes, long ofBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ofBytes);

        if (bytes >= ofBytes)
        {
            return "all";
        }

        var percent = (int)Math.Round(100.0 * bytes / ofBytes, MidpointRounding.AwayFromZero);

        return bytes > 0 && percent == 0 ? "under 1%"
            : percent == 100 ? "over 99%"
            : $"{percent}%";
    }
}
