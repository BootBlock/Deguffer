using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's walk, once: a bounded set of workers, each listing a directory and queueing what is below
/// it. <see cref="WalkWorkers{TState}"/> schedules them.
///
/// <para>This exists because several callers need the same traversal and differ only in what they do
/// with what it finds — <see cref="ParallelEnumerationScanner"/> adds each file's length,
/// <see cref="HardLinkAwareScanner"/> asks the file whether anything else links it, and
/// <see cref="Exploring.WalkExploreReader"/> records the shape of the tree itself. Written once per
/// caller, the traversal would carry a copy each of two safety rules: §5.3's "access denied is
/// normal, skip silently" and the refusal to follow a reparse point into a tree the caller never
/// classified. A safety rule in several places is one that gets corrected in one of them.</para>
/// </summary>
internal static class BoundedFileWalk
{
    /// <summary>
    /// How often the walk reports progress while it runs. §5.5 streams partial totals, and the UI
    /// cannot use thousands of updates a second: marshalling them would cost more than the walk.
    /// </summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// <see cref="Visit{TState}(string, TState, WalkTuning, Action{TState, DirectoryContents, Action{WalkEntry, TState}}, Action, TimeProvider, CancellationToken)"/>
    /// with the values <paramref name="tuning"/> resolves for the drive that holds
    /// <paramref name="root"/>, for a caller walking one root that has no tuning of its own to hand.
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        ScanTuner tuning,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct) =>
        Visit(root, rootState, tuning.For(root).Walk, onDirectory, onProgress, clock, ct);

    /// <summary>
    /// Visit every directory at or below <paramref name="root"/>, carrying a caller-chosen value
    /// down the tree with each one. The walk never reaches anything under a directory it was refused
    /// (§5.3), or anything under a reparse point.
    ///
    /// <para>The value is how a caller building a structure keeps its place: it hands each child
    /// directory whatever identifies the parent it just recorded, and gets it back when that child
    /// is reached. Without it the only way to relate a file to its directory is to look the path up
    /// in a dictionary once per file, which across millions of files is a locked lookup each (G4).
    /// </para>
    ///
    /// <paramref name="onDirectory"/> is called concurrently, and the contents it is handed are valid
    /// only while it runs. Nothing is descended into unless it asks: the third argument is how it says
    /// so, and a link is never a legitimate argument to it — the walk holds that rule, not the caller.
    /// Every directory is listed in the extended-length form §6.3 requires, so every entry it is
    /// handed carries the prefix.
    ///
    /// <para><paramref name="onProgress"/> is called once the first directory has been read, then at
    /// most once per <see cref="ProgressInterval"/> of <paramref name="clock"/> while the walk runs, and
    /// once at the end. It is never called beside itself, and never after this returns.</para>
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        WalkTuning tuning,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct) =>
        Visit(root, rootState, tuning, onDirectory, _ => onProgress(), clock, ct);

    /// <summary>
    /// <see cref="Visit{TState}(string, TState, WalkTuning, Action{TState, DirectoryContents, Action{WalkEntry, TState}}, Action, TimeProvider, CancellationToken)"/>,
    /// telling <paramref name="onProgress"/> whether the walk has finished: true only for the report
    /// made once at the end.
    ///
    /// <para>For a caller whose report starts work that only a walk still running needs, such as a
    /// picture of the tree so far, which the finished tree replaces as soon as it is built.</para>
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        WalkTuning tuning,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action<bool> onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        Visit(root, rootState, tuning, WalkListing.FromDisk, onDirectory, onProgress, clock, ct);
    }

    /// <summary>
    /// The walk above, listing each directory through <paramref name="list"/> rather than from the
    /// disk: the seam a test lists a tree no volume holds through, the benchmark counts the listings
    /// in flight through, and a removal lists through <see cref="IFileSystem"/>, which its tests
    /// observe and refuse through.
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        WalkTuning tuning,
        ListDirectory list,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action<bool> onProgress,
        TimeProvider clock,
        CancellationToken ct) =>
        Visit([(root, rootState)], tuning, list, onDirectory, onProgress, clock, ct);

    /// <summary>
    /// The walk above over several roots at once, each with its own value, sharing one set of
    /// workers. For a caller with many small trees to read, each of which alone would leave most of
    /// the threads idle. See <see cref="RootSetWalk"/>.
    /// </summary>
    public static void Visit<TState>(
        IReadOnlyList<(string Root, TState State)> roots,
        WalkTuning tuning,
        ListDirectory list,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action<bool> onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        var buffer = new ListingBuffer(tuning.ListingBufferBytes);

        new WalkWorkers<TState>(
                tuning.Threads,
                descend => DiskReader(new WalkListing(buffer, list), onDirectory, descend),
                onProgress,
                clock,
                ct)
            .Run([.. roots.Select(root => (LongPath.Extended(root.Root), root.State))]);
    }

    /// <summary>One worker's reading of a directory: its own listing, handed to the caller's callback.</summary>
    private static Action<string, TState> DiskReader<TState>(
        WalkListing listing,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action<string, TState> descend)
    {
        Action<WalkEntry, TState> descendUnlessLink = (directory, state) =>
        {
            // The walk holds this rule rather than trusting the caller to, because the caller is the
            // half that changes. A junction's target keeps its own place on the volume, so descending
            // through one both counts it twice and describes a tree nothing classified — and a link is
            // handed back rather than dropped, so a caller iterating the wrong list is a mistake that
            // can be made.
            if (!directory.IsReparsePoint)
            {
                descend(directory.FullName, state);
            }
        };

        return (path, state) => onDirectory(state, listing.Read(path), descendUnlessLink);
    }
}
