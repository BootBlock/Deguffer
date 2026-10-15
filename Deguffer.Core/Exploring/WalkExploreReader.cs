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
internal delegate bool LeavesFolderUnlisted(string name);

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
    /// returns. Some reports also carry a snapshot, where <paramref name="assembleSnapshot"/> says
    /// how to assemble one: see <see cref="WalkSnapshots"/> for when.
    /// </summary>
    /// <param name="unlisted">
    /// The folders not to list, or null to list every one. A folder left unlisted is still in the
    /// tree, with nothing below it, so a caller going through the tree meets it and can name it; and
    /// it is never marked as refused, because nothing asked Windows for its listing.
    /// </param>
    /// <param name="assembleSnapshot">
    /// How a snapshot is assembled (<see cref="WalkSnapshots.InBackground"/> for a scan), or null to
    /// take none, for a caller that draws nothing until the walk is over.
    /// </param>
    public static ExploreTree Read(
        string root,
        ScanTuner tuning,
        IOccupancyProbe probe,
        IProgress<ExploreProgress>? progress,
        TimeProvider clock,
        CancellationToken ct,
        LeavesUnlisted? unlisted = null,
        Func<Action, Task>? assembleSnapshot = null) =>
        Read(root, tuning.For(LongPath.Extended(root)).Walk, probe, progress, clock, ct, unlisted, assembleSnapshot);

    /// <summary>
    /// <see cref="Read(string, ScanTuner, IOccupancyProbe, IProgress{ExploreProgress}?, TimeProvider, CancellationToken, LeavesUnlisted?, Func{Action, Task}?)"/>
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
        Func<Action, Task>? assembleSnapshot = null)
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

        var snapshots = progress is not null && assembleSnapshot is not null
            ? new WalkSnapshots(builder, clock, assembleSnapshot)
            : null;

        try
        {
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
                        // Cleared, because each child holds its name and the pool would otherwise keep
                        // it alive.
                        children.Clear();
                        ArrayPool<ExploreChild>.Shared.Return(rented);
                    }

                    Interlocked.Add(ref items, count);
                    Interlocked.Add(ref bytes, size);

                    // Every entry of one listing shares its directory, so what is left unlisted in it
                    // is asked of the directory once.
                    LeavesFolderUnlisted? leaves = null;

                    for (var i = 0; i < contents.Entries.Count; i++)
                    {
                        var entry = contents.Entries[i];

                        if (!entry.IsDirectory)
                        {
                            continue;
                        }

                        if (unlisted is not null)
                        {
                            leaves ??= unlisted(LongPath.Display(entry.Directory), parent == ExploreTreeBuilder.RootNode);

                            if (leaves(entry.Name))
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

        // Indexed rather than enumerated, because an enumerator taken through the interface is boxed.
        for (var i = 0; i < contents.Entries.Count; i++)
        {
            var entry = contents.Entries[i];
            if (entry.IsDirectory)
            {
                into[next++] = new ExploreChild(
                    entry.Name, IsDirectory: true, IsLink: false, Size: 0, CreatedOf(entry), WrittenOf(entry),
                    Visibility: VisibilityOf(entry));
            }
            else
            {
                var file = File(entry, probe);
                into[next++] = file;
                bytes += file.Size;
            }
        }

        for (var i = 0; i < contents.Links.Count; i++)
        {
            var link = contents.Links[i];

            // Shown, and empty. Its target holds its own place in this tree, so counting anything
            // here would count those bytes twice — and hiding it altogether makes a directory the
            // user can plainly see in Explorer vanish from the picture.
            //
            // Dated, though. The dates are the link's own rather than its target's, which is the
            // right answer for the same reason the size is zero: the target is somewhere else in
            // this tree, carrying its own.
            into[next++] = new ExploreChild(
                link.Name, IsDirectory: true, IsLink: true, Size: 0, CreatedOf(link), WrittenOf(link),
                Visibility: VisibilityOf(link));
        }

        // Drawn like any other file, as the file table draws them. Leaving them out drew nothing for
        // a deduplicated file that holds its content, and only a symbolic link among them is a link.
        for (var i = 0; i < contents.ReparseFiles.Count; i++)
        {
            var file = File(contents.ReparseFiles[i], probe);
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
