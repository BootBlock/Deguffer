using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// How a node of a scanned tree is worded wherever the page names it: in its rows, on the line under
/// its map and to a screen reader. What kind of thing it is, its size, what its space on disk leaves
/// out, and its age.
///
/// <para>Every rule here is about honesty rather than about navigation: where a figure is a bound
/// rather than a measurement, and in which direction it can be wrong. In Core so that each is proved
/// without a window, beside <see cref="FreeSpace"/> and <see cref="RelativeAge"/>, which it composes
/// (G1).</para>
/// </summary>
public static class ExploreNodeText
{
    /// <summary>What kind of thing <paramref name="node"/> is, as a screen reader names it.</summary>
    public static string Kind(ExploreTree tree, int node) => tree.IsDirectory(node) ? "Folder" : "File";

    /// <summary>
    /// A node's size, marked where it is a lower bound.
    ///
    /// <para>A directory the walk was refused totals only what it could see, and the plain figure
    /// reads as a measurement. This mark is where the page says so (§7.1), on the row a reader acts
    /// on, such as the one for <c>System Volume Information</c> showing "0 B". A row says the same
    /// in words for a screen reader, because a symbol read aloud is not a sentence.</para>
    /// </summary>
    public static string Size(ExploreTree tree, int node) =>
        tree.HasUnknownSizeBelow(node)
            ? "≥ " + FreeSpace.Format(tree.SizeOf(node))
            : FreeSpace.Format(tree.SizeOf(node));

    /// <summary>
    /// What a node's space on disk leaves out: its length, where that reads differently, and why the
    /// two part. Empty where there is nothing to add, which is most files.
    ///
    /// <para>The length is compared as it is shown rather than to the byte. On the file table every
    /// file's space on disk is rounded up to whole clusters, and "4.0 KB on disk, 4.0 KB long" tells
    /// the reader nothing.</para>
    ///
    /// <para>A folder holds the reasons found anywhere under it, so it says it holds such files
    /// rather than that it is one.</para>
    /// </summary>
    public static string Storage(ExploreTree tree, int node)
    {
        var parts = new List<string>(2);

        var length = FreeSpace.Format(tree.LengthOf(node));
        if (length != FreeSpace.Format(tree.SizeOf(node)))
        {
            parts.Add($"{length} long");
        }

        if (Kinds(tree.StorageOf(node)) is { } kinds)
        {
            parts.Add(tree.IsDirectory(node) ? $"holds {kinds} files" : kinds);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// A node's age, marked where the scan could not see under it.
    ///
    /// <para>A directory the walk was refused contributes only its own timestamp to the roll-up,
    /// and a directory's own timestamp moves when its layout changes rather than when its contents
    /// do, so a log being appended to right now behind a refused listing reads as years idle.
    /// <c>DirectoryAge</c> answers "unknown" in that position rather than risk it, which is the right
    /// trade for a row that prices a deletion and the wrong one here: nearly every drive has some
    /// corner it cannot read, so blanking the age would blank the drive root and every folder above
    /// the refusal.</para>
    ///
    /// <para>So the date is kept and qualified, in the direction the error actually runs. The age
    /// shown can only be too old, never too new: the same asymmetry <see cref="Size"/> marks with
    /// "≥", and stated in words rather than a symbol because a screen reader reads this one
    /// aloud.</para>
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read, so one list is dated against one instant and two rows a
    /// millisecond apart cannot land in different days.
    /// </param>
    public static string Age(ExploreTree tree, int node, DateTime now)
    {
        var written = tree.ModifiedOf(node);
        var age = RelativeAge.Describe(written.Utc, now);

        return written.IsKnown && tree.HasUnknownSizeBelow(node) ? $"{age} or newer" : age;
    }

    /// <summary>The kinds of storage <paramref name="storage"/> names, in words, or null for none.</summary>
    private static string? Kinds(FileStorage storage)
    {
        var kinds = new List<string>(3);

        if (storage.HasFlag(FileStorage.CloudOnly))
        {
            kinds.Add("online-only");
        }

        if (storage.HasFlag(FileStorage.Compressed))
        {
            kinds.Add("compressed");
        }

        if (storage.HasFlag(FileStorage.Sparse))
        {
            kinds.Add("sparse");
        }

        return kinds.Count switch
        {
            0 => null,
            1 => kinds[0],
            _ => $"{string.Join(", ", kinds[..^1])} and {kinds[^1]}",
        };
    }
}
