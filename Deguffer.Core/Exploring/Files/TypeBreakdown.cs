using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Files;

/// <summary>How much of one kind of file a folder holds, and which extensions make most of it up.</summary>
/// <param name="Bytes">
/// Every byte of the kind below the folder. Can be more than <paramref name="Extensions"/> add up to,
/// which lists only the largest of them.
/// </param>
/// <param name="Files">How many files of the kind are below the folder.</param>
/// <param name="Extensions">The largest extensions of the kind, largest first.</param>
public sealed record TypeShare(FileCategory Category, long Bytes, int Files, IReadOnlyList<ExtensionShare> Extensions);

/// <summary>How much one extension of one kind of file adds up to below a folder.</summary>
/// <param name="Extension">
/// The extension with its leading dot, in lower case, or empty for a name that has none.
/// </param>
public readonly record struct ExtensionShare(string Extension, long Bytes, int Files);

/// <summary>
/// What kind of file fills one folder: its bytes and its files by <see cref="FileCategory"/>, largest
/// first, with the largest extensions inside each.
///
/// <para>A description, never a classification (§7.1). The order is by size and nothing else, and a
/// kind of file says what the files are, never that they can go.</para>
///
/// <para>Every byte the tree counts below the folder is in exactly one share, so the shares add up to
/// the folder's own total. That is what lets the breakdown sit beside a map drawn from the same tree
/// without the two disagreeing. A link's bytes are in the share its name says, because the tree
/// counts them in its folder's total. The bytes a folder holds of its own, which a file table can
/// record and a walk cannot, are <see cref="FileCategory.Other"/>, because no name says what they
/// are.</para>
/// </summary>
public sealed class TypeBreakdown
{
    /// <summary>How many extensions each share names. Past five a share reads as a list rather than an answer.</summary>
    private const int ExtensionsPerShare = 5;

    /// <summary>How many nodes are visited between two looks at the cancellation token, as in <see cref="LargestFiles"/>.</summary>
    private const int CancellationStride = 4096;

    private TypeBreakdown(ExploreTree tree, int root, IReadOnlyList<TypeShare> shares)
    {
        Tree = tree;
        Root = root;
        Shares = shares;
    }

    public ExploreTree Tree { get; }

    /// <summary>The folder broken down.</summary>
    public int Root { get; }

    /// <summary>
    /// Every kind of file the folder holds any of, largest first. Of two the same size, the one
    /// earlier in <see cref="FileCategories.All"/> comes first, which is the rule
    /// <see cref="DominantTypes"/> breaks a tie by, so where the folder holds any bytes the first share
    /// here is the kind a map
    /// coloured by type paints the folder.
    /// </summary>
    public IReadOnlyList<TypeShare> Shares { get; }

    /// <summary>
    /// Break <paramref name="root"/> of <paramref name="tree"/> down by kind of file, in one pass over
    /// everything below it.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled during the pass.</exception>
    public static TypeBreakdown Measure(ExploreTree tree, int root, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var count = FileCategories.All.Count;
        var bytes = new long[count];
        var files = new int[count];

        // One table per kind rather than one for every kind, because an extension does not always name
        // one kind: an .exe is an installer or a program by the rest of its name.
        var extensions = new Dictionary<string, Tally>[count];

        var waiting = new Stack<int>();
        var visited = 0;

        waiting.Push(root);

        // Iterative for the reason ExploreTree's own passes are: a deep node_modules tree overflows
        // the stack of a recursive one.
        while (waiting.TryPop(out var node))
        {
            if (++visited % CancellationStride == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (tree.IsDirectory(node))
            {
                bytes[(int)FileCategory.Other] += OwnBytes(tree, node, waiting);
                continue;
            }

            var name = tree.NameOf(node);
            var category = (int)FileCategories.Of(name);

            bytes[category] += tree.SizeOf(node);
            files[category]++;

            ref var tally = ref Tallied(extensions[category] ??= new(StringComparer.OrdinalIgnoreCase), name);
            tally = new Tally(tally.Bytes + tree.SizeOf(node), tally.Files + 1);
        }

        var shares = new List<TypeShare>(count);

        foreach (var category in FileCategories.All)
        {
            var at = (int)category;

            if (bytes[at] > 0 || files[at] > 0)
            {
                shares.Add(new TypeShare(category, bytes[at], files[at], Largest(extensions[at])));
            }
        }

        // Stable, so a tie keeps the order of FileCategories.All that the list was built in.
        TypeShare[] ordered = [.. shares.OrderByDescending(share => share.Bytes)];

        return new TypeBreakdown(tree, root, ordered);
    }

    /// <summary>
    /// Queue <paramref name="folder"/>'s children, and answer the bytes it holds beyond theirs: none on
    /// a walk, and whatever the file table records for the folder itself otherwise.
    /// </summary>
    private static long OwnBytes(ExploreTree tree, int folder, Stack<int> waiting)
    {
        var own = tree.SizeOf(folder);

        foreach (var child in tree.ChildrenOf(folder))
        {
            waiting.Push(child);
            own -= tree.SizeOf(child);
        }

        return own;
    }

    /// <summary>
    /// The tally for <paramref name="name"/>'s extension, added where it is new. Read by span, so a name
    /// whose extension has been seen allocates nothing, and a pass over a volume sees each extension
    /// many thousands of times (G5).
    /// </summary>
    private static ref Tally Tallied(Dictionary<string, Tally> table, string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot >= 0 && dot < name.Length - 1 ? name.AsSpan(dot) : [];

        return ref CollectionsMarshal.GetValueRefOrAddDefault(
            table.GetAlternateLookup<ReadOnlySpan<char>>(), extension, out _);
    }

    /// <summary>The largest few of one kind's extensions, largest first, and of two the same size, by name.</summary>
    private static ExtensionShare[] Largest(Dictionary<string, Tally>? table) => table is null
        ? []
        : [
            .. table
                .OrderByDescending(entry => entry.Value.Bytes)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Take(ExtensionsPerShare)
                .Select(entry => new ExtensionShare(entry.Key.ToLowerInvariant(), entry.Value.Bytes, entry.Value.Files)),
        ];

    private readonly record struct Tally(long Bytes, int Files);
}
