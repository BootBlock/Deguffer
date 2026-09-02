using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Builds an <see cref="ExploreTree"/> by walking directories — §5.5's guaranteed route, the one
/// that needs no rights beyond the ones the user already has.
///
/// <para>Slower than the file table and correct everywhere it is slower. It is what answers for a
/// volume that is not NTFS, for a process that is not elevated, and for a share with no drive
/// letter to open, which between them is most of how Deguffer actually runs (§6.3).</para>
/// </summary>
internal static class WalkExploreReader
{
    /// <summary>
    /// Walk <paramref name="root"/> and return everything under it.
    ///
    /// <paramref name="onLevel"/> is called once per breadth-first level with the running counts,
    /// which is the cadence §5.5 wants for a UI: a level is coarse enough to be worth marshalling
    /// and frequent enough that a large scan does not look stalled.
    /// </summary>
    public static ExploreTree Read(
        string root,
        Action<ExploreTreeBuilder, long, long>? onLevel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // §6.3: the walk is given the extended-length form, and .NET builds every child path from
        // the parent it was handed — so the whole traversal stays past MAX_PATH. The tree keeps the
        // ordinary form, because every path it hands back is one a person reads or a shell opens.
        var builder = new ExploreTreeBuilder(LongPath.Display(root));

        long items = 0;
        long bytes = 0;

        BoundedFileWalk.Visit(
            LongPath.Extended(root),
            ExploreTreeBuilder.RootNode,
            (parent, contents, descend) =>
            {
                if (contents.WasRefused)
                {
                    // §5.3 keeps the walk quiet about this, and it stays quiet — but the bytes
                    // behind a refused directory are real, so the totals above it are marked as
                    // lower bounds rather than presented as measurements.
                    //
                    // Whatever was listed before the refusal is still recorded below rather than
                    // discarded. Enumeration can fail part way through, and the entries already in
                    // hand are as real as any others — dropping them would make the picture worse
                    // and the total shorter for no gain, and it is what the file-callback walk
                    // does with them too.
                    builder.MarkSizeUnknown(parent);
                }

                var children = Describe(contents);
                if (children.Count == 0)
                {
                    return;
                }

                var first = builder.AddChildren(parent, children);

                Interlocked.Add(ref items, children.Count);
                Interlocked.Add(ref bytes, children.Sum(c => c.Size));

                for (var i = 0; i < contents.Entries.Count; i++)
                {
                    if (contents.Entries[i] is DirectoryInfo directory)
                    {
                        descend(directory, first + i);
                    }
                }
            },
            () => onLevel?.Invoke(builder, Interlocked.Read(ref items), Interlocked.Read(ref bytes)),
            ct);

        return builder.Build();
    }

    /// <summary>
    /// One directory's entries in the order the tree will hold them: the ordinary children first,
    /// then the links.
    ///
    /// <para>The order is load-bearing rather than cosmetic. <see cref="ExploreTreeBuilder.AddChildren"/>
    /// numbers what it is given consecutively, so an entry's index in
    /// <see cref="DirectoryContents.Entries"/> is its offset from the first node number — which is
    /// what lets the caller descend into a child directory without this having to hand back a map.
    /// Putting the links first would silently shift every one of those numbers by the number of
    /// junctions in the directory.</para>
    /// </summary>
    private static List<ExploreChild> Describe(DirectoryContents contents)
    {
        var children = new List<ExploreChild>(contents.Entries.Count + contents.Links.Count);

        foreach (var entry in contents.Entries)
        {
            children.Add(entry is FileInfo file
                ? new ExploreChild(file.Name, IsDirectory: false, IsLink: false, Length(file))
                : new ExploreChild(entry.Name, IsDirectory: true, IsLink: false, Size: 0));
        }

        foreach (var link in contents.Links)
        {
            // Shown, and empty. Its target holds its own place in this tree, so counting anything
            // here would count those bytes twice — and hiding it altogether makes a directory the
            // user can plainly see in Explorer vanish from the picture.
            children.Add(new ExploreChild(link.Name, IsDirectory: true, IsLink: true, Size: 0));
        }

        return children;
    }

    /// <summary>
    /// A file's length, or zero where it cannot be read.
    ///
    /// <para><see cref="FileInfo.Length"/> throws for a file deleted between the enumeration and
    /// this call, which on a live machine is ordinary rather than exceptional — a build writing to
    /// a temp directory produces it constantly. Zero is the honest answer for a file that is no
    /// longer there, and it must not take the scan down.</para>
    /// </summary>
    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
