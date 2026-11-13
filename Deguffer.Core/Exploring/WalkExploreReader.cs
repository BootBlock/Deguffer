using System.Buffers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Which of the folders in <paramref name="parent"/> a walk leaves unlisted, with everything in them:
/// places its caller passes over, where listing them would cost the walk time and tell the caller
/// nothing. Asked once for each listed folder that holds a folder, so what depends on the folder
/// alone is worked out once rather than once for each folder in it.
/// </summary>
/// <param name="parent">The folder holding them, in display form.</param>
/// <param name="parentIsRoot">Whether <paramref name="parent"/> is the folder the walk began at.</param>
internal delegate LeavesFolderUnlisted LeavesUnlisted(string parent, bool parentIsRoot);

/// <summary>Whether a walk leaves the folder named <paramref name="name"/>, in the folder it was asked of, unlisted.</summary>
internal delegate bool LeavesFolderUnlisted(ReadOnlySpan<char> name);

/// <summary>
/// A walk of <paramref name="root"/> into a tree, as
/// <see cref="WalkExploreReader.Read(string, ScanTuner, IOccupancyProbe, IProgress{ExploreProgress}?, TimeProvider, CancellationToken, LeavesUnlisted?, SnapshotAssembly?)"/>
/// does it.
/// </summary>
internal delegate ExploreTree ExploreWalk(
    string root,
    ScanTuner tuning,
    IOccupancyProbe probe,
    IProgress<ExploreProgress>? progress,
    TimeProvider clock,
    CancellationToken ct,
    LeavesUnlisted? unlisted,
    SnapshotAssembly? snapshotAssembly);

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
    /// <paramref name="progress"/> is told the running counts at the walk's
    /// <see cref="BoundedFileWalk.ProgressInterval"/> of <paramref name="clock"/>, which is the
    /// cadence §5.5 wants for a UI: coarse enough to be worth marshalling and frequent enough that a
    /// large scan does not look stalled. It is never told beside itself, and never after this
    /// returns. Some reports also carry a snapshot, where <paramref name="snapshotAssembly"/> says
    /// how to make one: see <see cref="ScanSnapshots"/> for when.
    /// </summary>
    /// <param name="unlisted">
    /// The folders not to list, or null to list every one. A folder left unlisted is still in the
    /// tree, with nothing below it, so a caller going through the tree meets it and can name it; and
    /// it is never marked as refused, because nothing asked Windows for its listing.
    /// </param>
    /// <param name="snapshotAssembly">
    /// How a snapshot is made, or null to take none, for a caller that draws nothing until the walk
    /// is over.
    /// </param>
    public static ExploreTree Read(
        string root,
        ScanTuner tuning,
        IOccupancyProbe probe,
        IProgress<ExploreProgress>? progress,
        TimeProvider clock,
        CancellationToken ct,
        LeavesUnlisted? unlisted = null,
        SnapshotAssembly? snapshotAssembly = null) =>
        Read(root, tuning.For(LongPath.Extended(root)).Walk, probe, progress, clock, ct, unlisted, snapshotAssembly);

    /// <summary>
    /// <see cref="Read(string, ScanTuner, IOccupancyProbe, IProgress{ExploreProgress}?, TimeProvider, CancellationToken, LeavesUnlisted?, SnapshotAssembly?)"/>
    /// with the walk's values given rather than resolved for the drive, for a benchmark that sets
    /// them itself.
    /// </summary>
    public static ExploreTree Read(
        string root,
        WalkTuning tuning,
        IOccupancyProbe probe,
        IProgress<ExploreProgress>? progress,
        TimeProvider clock,
        CancellationToken ct,
        LeavesUnlisted? unlisted = null,
        SnapshotAssembly? snapshotAssembly = null) =>
        Read(root, tuning, DirectoryQuery.List, probe, progress, clock, ct, unlisted, snapshotAssembly);

    /// <summary>
    /// The walk above, listing each directory through <paramref name="list"/> rather than from the
    /// disk: the seam a test lists a tree no volume holds through.
    /// </summary>
    internal static ExploreTree Read(
        string root,
        WalkTuning tuning,
        ListDirectory list,
        IOccupancyProbe probe,
        IProgress<ExploreProgress>? progress,
        TimeProvider clock,
        CancellationToken ct,
        LeavesUnlisted? unlisted = null,
        SnapshotAssembly? snapshotAssembly = null)
    {
        // §6.3: the walk is given the extended-length form, and .NET builds every child path from
        // the parent it was handed — so the whole traversal stays past MAX_PATH. The tree keeps the
        // ordinary form, because every path it hands back is one a person reads or a shell opens.
        var (created, written) = RootTimes(LongPath.Extended(root));

        var builder = new ExploreTreeBuilder(LongPath.Display(root), created, written);

        long items = 0;
        long bytes = 0;

        // Kept from one snapshot to the next, so each copies out only the nodes added since the last.
        var shared = new SnapshotColumns();

        var snapshots = progress is not null && snapshotAssembly is not null
            ? new ScanSnapshots(
                order =>
                {
                    var captured = builder.Capture();
                    return stopped => captured.Snapshot(order, shared, stopped);
                },
                clock,
                snapshotAssembly)
            : null;

        try
        {
            BoundedFileWalk.Visit(
                LongPath.Extended(root),
                ExploreTreeBuilder.RootNode,
                tuning,
                // Lent rather than built as strings: every name is copied into the tree's pool before
                // the listing that lent it is over, so a string for each would only be garbage.
                FileNames.AllLent,
                list,
                (parent, contents, descend) =>
                {
                    if (contents.WasRefused)
                    {
                        // §5.3 keeps the walk quiet about this, and it stays quiet — but the bytes
                        // behind a refused directory are real, so the totals above it are marked as
                        // lower bounds rather than presented as measurements, and the directory is
                        // named, so a caller going through its files can tell it from an empty one.
                        //
                        // Whatever was listed before the refusal is still recorded below rather than
                        // discarded. Enumeration can fail part way through, and the entries already in
                        // hand are as real as any others — dropping them would make the picture worse
                        // and the total shorter for no gain, and it is what the file-callback walk
                        // does with them too.
                        builder.MarkListingRefused(parent);
                    }

                    var count = contents.Count;
                    if (count == 0)
                    {
                        return;
                    }

                    // Rented rather than allocated for each directory: a volume holds hundreds of
                    // thousands of them, and one of a few thousand entries would put its array on the
                    // large object heap, where allocating can start a full collection that traces the
                    // whole tree built so far (G4). The builder copies it before it returns.
                    var rented = ArrayPool<ExploreChild>.Shared.Rent(count);
                    var children = rented.AsSpan(0, count);
                    int first;
                    long size;

                    try
                    {
                        size = Describe(contents, probe, children);
                        first = builder.AddChildren(parent, children);
                    }
                    finally
                    {
                        // Cleared, because each child holds its name, and the pool would otherwise keep
                        // it alive, or the listing memory a lent name is read from.
                        children.Clear();
                        ArrayPool<ExploreChild>.Shared.Return(rented);
                    }

                    Interlocked.Add(ref items, count);
                    Interlocked.Add(ref bytes, size);

                    // What is left unlisted in a listing is asked of its directory once.
                    LeavesFolderUnlisted? leaves = null;

                    var entries = contents.Entries;

                    for (var i = 0; i < entries.Length; i++)
                    {
                        var entry = entries[i];

                        if (!entry.IsDirectory)
                        {
                            continue;
                        }

                        if (unlisted is not null)
                        {
                            leaves ??= unlisted(LongPath.Display(contents.Directory), parent == ExploreTreeBuilder.RootNode);

                            if (leaves(entry.NameSpan))
                            {
                                continue;
                            }
                        }

                        descend(entry, first + i);
                    }
                },
                finished => progress?.Report(new ExploreProgress(
                    Interlocked.Read(ref items),
                    Total: null,
                    Interlocked.Read(ref bytes),
                    // None from the last report: the finished tree is built next, and replaces it.
                    finished ? null : snapshots?.Take())),
                clock,
                ct);
        }
        finally
        {
            snapshots?.Stop();
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// Write one directory's entries into <paramref name="into"/> in the order the tree will hold
    /// them: the ordinary children first, then the links, then the files carrying a reparse point.
    /// Returns the bytes they occupy, added up while they are written rather than in a second pass.
    ///
    /// <para>The order is load-bearing rather than cosmetic. <see cref="ExploreTreeBuilder.AddChildren"/>
    /// numbers what it is given consecutively, so an entry's index in
    /// <see cref="DirectoryContents.Entries"/> is its offset from the first node number — which is
    /// what lets the caller descend into a child directory without this having to hand back a map.
    /// Putting anything first would silently shift every one of those numbers.</para>
    /// </summary>
    /// <param name="into">Exactly <see cref="DirectoryContents.Count"/> long.</param>
    internal static long Describe(DirectoryContents contents, IOccupancyProbe probe, Span<ExploreChild> into)
    {
        var next = 0;
        long bytes = 0;

        foreach (var entry in contents.Entries)
        {
            if (entry.IsDirectory)
            {
                into[next++] = new ExploreChild(
                    entry.KeptName, IsDirectory: true, IsLink: false, Size: 0, CreatedOf(entry), WrittenOf(entry),
                    Visibility: VisibilityOf(entry));
            }
            else
            {
                var file = File(entry, probe);
                into[next++] = file;
                bytes += file.Size;
            }
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
            into[next++] = new ExploreChild(
                link.KeptName, IsDirectory: true, IsLink: true, Size: 0, CreatedOf(link), WrittenOf(link),
                Visibility: VisibilityOf(link));
        }

        // Drawn like any other file, as the file table draws them. Leaving them out drew nothing for
        // a deduplicated file that holds its content, and only a symbolic link among them is a link.
        foreach (var reparseFile in contents.ReparseFiles)
        {
            var file = File(reparseFile, probe);
            into[next++] = file;
            bytes += file.Size;
        }

        return bytes;
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
                entry.KeptName, IsDirectory: false, IsLink: false, entry.Length, CreatedOf(entry), WrittenOf(entry),
                Visibility: VisibilityOf(entry));
        }

        var storage = StorageAttributes.Of(entry.Attributes);

        return probe.Measure(entry.FullName, entry.Attributes) switch
        {
            { IsLink: true } => new ExploreChild(
                entry.KeptName, IsDirectory: false, IsLink: true, Size: 0, CreatedOf(entry), WrittenOf(entry), Length: 0,
                Visibility: VisibilityOf(entry)),
            { } occupied => new ExploreChild(
                entry.KeptName, IsDirectory: false, IsLink: false, occupied.Bytes, CreatedOf(entry), WrittenOf(entry),
                entry.Length, storage, Visibility: VisibilityOf(entry)),
            null => new ExploreChild(
                entry.KeptName, IsDirectory: false, IsLink: false, Size: 0, CreatedOf(entry), WrittenOf(entry),
                entry.Length, storage, SizeUnknown: true, VisibilityOf(entry)),
        };
    }

    private static ExploreTimestamp CreatedOf(WalkEntry entry) => ExploreTimestamp.FromFileTime(entry.CreationFileTime);

    private static ExploreTimestamp WrittenOf(WalkEntry entry) => ExploreTimestamp.FromFileTime(entry.LastWriteFileTime);

    private static FileVisibility VisibilityOf(WalkEntry entry) => FileVisibilities.Of(entry.Attributes);

    /// <summary>
    /// When the root was made and last written, each unknown where nothing could say.
    ///
    /// <para><b>Only the root is read this way.</b> Every other entry's timestamps come from the
    /// listing that found it, so they cost nothing and cannot fail. The root is the one entry nothing
    /// listed, so this read goes to the disk — and against a share that has gone away that is a real
    /// network round trip that fails rather than answering. A scan is not worth failing over a date:
    /// §5.3 already makes an unreadable path ordinary, the walk below still reports what it could
    /// reach, and the honest answer for a location nothing can open is that its age is not known.</para>
    ///
    /// <para>Read as FILETIMEs, as the listing reads every other entry, so a root dated past the year
    /// 9999 is unknown rather than a <see cref="DateTime"/> that throws. A time Windows never set
    /// answers zero, which <see cref="ExploreTimestamp"/> reads as unknown too.
    /// <see cref="Providers.DirectoryAge"/> guards the same value for the same reason: January 1601
    /// in an age column is the oldest invitation there is to delete something.</para>
    /// </summary>
    private static (ExploreTimestamp Created, ExploreTimestamp Written) RootTimes(string extended) =>
        FileAttributeRead.Read(extended, out _, out _, out var created, out var written) == 0
            ? (ExploreTimestamp.FromFileTime(created), ExploreTimestamp.FromFileTime(written))
            : (ExploreTimestamp.Unknown, ExploreTimestamp.Unknown);
}
