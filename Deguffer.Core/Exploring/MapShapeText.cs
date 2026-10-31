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
/// <param name="LowerBound">
/// Whether its size is a lower bound, because the scan could not read all of it, so its name marks
/// the size as its label and its row do (§7.1).
/// </param>
public sealed record MapShapeFacts(string Name, string Kind, bool Opens, bool LowerBound)
{
    /// <summary>
    /// What a screen reader is told of <paramref name="node"/> of <paramref name="asked"/>, where that
    /// is the tree on screen and holds it (<see cref="ShownTree"/>); otherwise null, so a shape of
    /// any other tree is named by the map alone.
    /// </summary>
    public static MapShapeFacts? For<TTree>(ISizedTree asked, TTree? shown, int node, IMapShapes<TTree> shapes)
        where TTree : class, IShownTree
    {
        ArgumentNullException.ThrowIfNull(shapes);

        return ShownTree.Of(asked, shown, node) is { } tree ? shapes.FactsOf(tree, node) : null;
    }
}

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
    /// <param name="lowerBound">Whether the size is a lower bound. See <see cref="ExploreNodeText.Size(ExploreTree, int)"/>.</param>
    public static string Name(string name, long bytes, long ofBytes, string? ofName, bool lowerBound)
    {
        ArgumentNullException.ThrowIfNull(name);

        var size = $"{name}, {ExploreNodeText.Size(bytes, lowerBound)}";

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
    /// What is written on a shape on the map: its name, then its size, apart by two spaces. One format
    /// for every page and every block, so a label reads alike wherever it is.
    /// </summary>
    public static string Label(string name, long bytes)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Labelled(name, FreeSpace.Format(bytes));
    }

    /// <summary>
    /// What is written on the shape of <paramref name="node"/>: its name, then its size, marked where
    /// the size is a lower bound (§7.1), as its row marks it.
    /// </summary>
    public static string Label(ExploreTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Labelled(tree.NameOf(node), ExploreNodeText.Size(tree, node));
    }

    private static string Labelled(string name, string size) => $"{name}  {size}";

    /// <summary>
    /// <paramref name="bytes"/> as a share of <paramref name="ofBytes"/>, to the nearest percent:
    /// "under 1%" rather than "0%" for a shape that is there, and "all" for one that is the whole of it.
    /// </summary>
    internal static string Share(long bytes, long ofBytes)
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
