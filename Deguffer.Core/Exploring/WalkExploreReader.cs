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
        // §6.3: the walk is given the extended-length form, and .NET builds every child path from
        // the parent it was handed — so the whole traversal stays past MAX_PATH. The tree keeps the
        // ordinary form, because every path it hands back is one a person reads or a shell opens.
        var top = new DirectoryInfo(LongPath.Extended(root));

        var builder = new ExploreTreeBuilder(
            LongPath.Display(root),
            Created(top),
            LastWritten(top));

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

        return builder.Build(ExploreChildOrder.BySize);
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
                ? new ExploreChild(
                    file.Name, IsDirectory: false, IsLink: false, Length(file),
                    Created(file), LastWritten(file))
                : new ExploreChild(
                    entry.Name, IsDirectory: true, IsLink: false, Size: 0,
                    Created(entry), LastWritten(entry)));
        }

        foreach (var link in contents.Links)
        {
            // Shown, and empty. Its target holds its own place in this tree, so counting anything
            // here would count those bytes twice — and hiding it altogether makes a directory the
            // user can plainly see in Explorer vanish from the picture.
            //
            // Dated, though. The dates are the link's own rather than its target's, which is the
            // right answer for the same reason the size is zero: the target is somewhere else in
            // this tree, carrying its own.
            children.Add(new ExploreChild(
                link.Name, IsDirectory: true, IsLink: true, Size: 0,
                Created(link), LastWritten(link)));
        }

        return children;
    }

    /// <summary>When the entry was made, or unknown where nothing could say.</summary>
    private static ExploreTimestamp Created(FileSystemInfo entry) =>
        TimeOf(entry, static e => e.CreationTimeUtc);

    /// <summary>When the entry itself was last written.</summary>
    private static ExploreTimestamp LastWritten(FileSystemInfo entry) =>
        TimeOf(entry, static e => e.LastWriteTimeUtc);

    /// <summary>
    /// One timestamp off an entry, or unknown where reading it fails.
    ///
    /// <para><b>Free for the entries, and that is why both routes can answer alike.</b> The
    /// enumeration already read the full directory record and .NET caches it on the instance, so
    /// for a child this is a field read rather than a second trip to the disk — which across
    /// millions of files is the difference between a column worth having and one that doubles the
    /// scan.</para>
    ///
    /// <para><b>Not free for the root, which is what the guard is here for.</b> The root is the one
    /// entry nothing enumerated, so the first of these reads is what initialises it — and against a
    /// share that has gone away that is a real network round trip that raises rather than answering.
    /// A scan is not worth failing over a date: §5.3 already makes an unreadable path ordinary, the
    /// walk below still reports what it could reach, and the honest answer for a location nothing
    /// can open is that its age is not known.</para>
    ///
    /// <para>An entry that has gone since the enumeration answers with the start of the Windows
    /// epoch rather than failing, and <see cref="ExploreTimestamp"/> reads that as unknown.
    /// <see cref="Providers.DirectoryAge"/> guards the same value for the same reason: January 1601
    /// in an age column is the oldest invitation there is to delete something.</para>
    /// </summary>
    private static ExploreTimestamp TimeOf(FileSystemInfo entry, Func<FileSystemInfo, DateTime> read)
    {
        try
        {
            return ExploreTimestamp.FromUtc(read(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExploreTimestamp.Unknown;
        }
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
