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
    /// Walk <paramref name="root"/> and return everything under it, asking <paramref name="probe"/>
    /// what each file occupies where its listing cannot say.
    ///
    /// <paramref name="onProgress"/> is called with the running counts at the walk's
    /// <see cref="BoundedFileWalk.ProgressInterval"/> of <paramref name="clock"/>, which is the
    /// cadence §5.5 wants for a UI: coarse enough to be worth marshalling and frequent enough that a
    /// large scan does not look stalled. It is never called beside itself.
    /// </summary>
    public static ExploreTree Read(
        string root,
        ScanTuner tuning,
        IOccupancyProbe probe,
        Action<ExploreTreeBuilder, long, long>? onProgress,
        TimeProvider clock,
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
            tuning,
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

                var children = Describe(contents, probe);
                if (children.Count == 0)
                {
                    return;
                }

                var first = builder.AddChildren(parent, children);

                Interlocked.Add(ref items, children.Count);
                Interlocked.Add(ref bytes, children.Sum(c => c.Size));

                for (var i = 0; i < contents.Entries.Count; i++)
                {
                    if (contents.Entries[i].IsDirectory)
                    {
                        descend(contents.Entries[i], first + i);
                    }
                }
            },
            () => onProgress?.Invoke(builder, Interlocked.Read(ref items), Interlocked.Read(ref bytes)),
            clock,
            ct);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// One directory's entries in the order the tree will hold them: the ordinary children first,
    /// then the links, then the files carrying a reparse point.
    ///
    /// <para>The order is load-bearing rather than cosmetic. <see cref="ExploreTreeBuilder.AddChildren"/>
    /// numbers what it is given consecutively, so an entry's index in
    /// <see cref="DirectoryContents.Entries"/> is its offset from the first node number — which is
    /// what lets the caller descend into a child directory without this having to hand back a map.
    /// Putting anything first would silently shift every one of those numbers.</para>
    /// </summary>
    internal static List<ExploreChild> Describe(DirectoryContents contents, IOccupancyProbe probe)
    {
        var children = new List<ExploreChild>(
            contents.Entries.Count + contents.Links.Count + contents.ReparseFiles.Count);

        foreach (var entry in contents.Entries)
        {
            children.Add(entry.IsDirectory
                ? new ExploreChild(
                    entry.Name, IsDirectory: true, IsLink: false, Size: 0, CreatedOf(entry), WrittenOf(entry),
                    Visibility: VisibilityOf(entry))
                : File(entry, probe));
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
                link.Name, IsDirectory: true, IsLink: true, Size: 0, CreatedOf(link), WrittenOf(link),
                Visibility: VisibilityOf(link)));
        }

        // Drawn like any other file, as the file table draws them. Leaving them out drew nothing for
        // a deduplicated file that holds its content, and only a symbolic link among them is a link.
        foreach (var file in contents.ReparseFiles)
        {
            children.Add(File(file, probe));
        }

        return children;
    }

    /// <summary>
    /// A file, at what it occupies. Its listing's length is that figure for every file whose
    /// attributes say nothing else, and those are asked nothing more, which keeps the walk to one
    /// listing per directory. Only a cloud, compressed, sparse or reparse-point file is measured.
    ///
    /// <para>One Windows would not measure is drawn at nothing and marked unknown, so every total
    /// above it says it is a lower bound. Drawing it at its length instead would be the overcount
    /// this measurement exists to stop.</para>
    /// </summary>
    private static ExploreChild File(WalkEntry entry, IOccupancyProbe probe)
    {
        if (!StorageAttributes.MayDifferFromLength(entry.Attributes))
        {
            return new ExploreChild(
                entry.Name, IsDirectory: false, IsLink: false, entry.Length, CreatedOf(entry), WrittenOf(entry),
                Visibility: VisibilityOf(entry));
        }

        var storage = StorageAttributes.Of(entry.Attributes);

        return probe.Measure(entry.FullName, entry.Attributes) switch
        {
            { IsLink: true } => new ExploreChild(
                entry.Name, IsDirectory: false, IsLink: true, Size: 0, CreatedOf(entry), WrittenOf(entry), Length: 0,
                Visibility: VisibilityOf(entry)),
            { } occupied => new ExploreChild(
                entry.Name, IsDirectory: false, IsLink: false, occupied.Bytes, CreatedOf(entry), WrittenOf(entry),
                entry.Length, storage, Visibility: VisibilityOf(entry)),
            null => new ExploreChild(
                entry.Name, IsDirectory: false, IsLink: false, Size: 0, CreatedOf(entry), WrittenOf(entry),
                entry.Length, storage, SizeUnknown: true, VisibilityOf(entry)),
        };
    }

    private static ExploreTimestamp CreatedOf(WalkEntry entry) => ExploreTimestamp.FromUtc(entry.CreationTimeUtc);

    private static ExploreTimestamp WrittenOf(WalkEntry entry) => ExploreTimestamp.FromUtc(entry.LastWriteTimeUtc);

    private static FileVisibility VisibilityOf(WalkEntry entry) => FileVisibilities.Of(entry.Attributes);

    /// <summary>When the root was made, or unknown where nothing could say.</summary>
    private static ExploreTimestamp Created(DirectoryInfo root) =>
        TimeOf(root, static r => r.CreationTimeUtc);

    /// <summary>When the root itself was last written.</summary>
    private static ExploreTimestamp LastWritten(DirectoryInfo root) =>
        TimeOf(root, static r => r.LastWriteTimeUtc);

    /// <summary>
    /// One timestamp off the root, or unknown where reading it fails.
    ///
    /// <para><b>Only the root is read this way.</b> Every other entry's timestamps come from the
    /// listing that found it, so they cost nothing and cannot fail. The root is the one entry nothing
    /// listed, so the first of these reads goes to the disk — and against a share that has gone away
    /// that is a real network round trip that raises rather than answering. A scan is not worth
    /// failing over a date: §5.3 already makes an unreadable path ordinary, the walk below still
    /// reports what it could reach, and the honest answer for a location nothing can open is that its
    /// age is not known.</para>
    ///
    /// <para>An entry that has gone since it was listed answers with the start of the Windows epoch
    /// rather than failing, and <see cref="ExploreTimestamp"/> reads that as unknown.
    /// <see cref="Providers.DirectoryAge"/> guards the same value for the same reason: January 1601
    /// in an age column is the oldest invitation there is to delete something.</para>
    /// </summary>
    private static ExploreTimestamp TimeOf(DirectoryInfo root, Func<DirectoryInfo, DateTime> read)
    {
        try
        {
            return ExploreTimestamp.FromUtc(read(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExploreTimestamp.Unknown;
        }
    }
}
