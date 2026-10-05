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
    /// Visit every file at or below <paramref name="root"/>, minus the two things this walk
    /// deliberately never reaches: anything under a directory it was refused (§5.3), and anything
    /// under a reparse point.
    ///
    /// <paramref name="onFile"/> is called concurrently from several threads, so what it does must
    /// be safe to do that way. <paramref name="onProgress"/> is called once the first directory has
    /// been read, then at most once per <see cref="ProgressInterval"/> of <paramref name="clock"/>
    /// while the walk runs, and once at the end. It is never called beside itself, and never after
    /// this returns.
    /// </summary>
    /// <param name="root">
    /// The directory to walk. Every directory is listed in the extended-length form §6.3 requires,
    /// so every path handed to <paramref name="onFile"/> carries the prefix.
    /// </param>
    /// <param name="onReparseFile">
    /// Called, concurrently, for each file carrying a reparse point, which <paramref name="onFile"/>
    /// never sees. See <see cref="DirectoryContents.ReparseFiles"/>.
    /// </param>
    /// <returns>
    /// Whether <paramref name="root"/> itself was listed. A folder refused below it is skipped as
    /// §5.3 says, and only the root's refusal is returned, because only that one leaves the caller
    /// with nothing measured. See <see cref="RootReach"/>.
    /// </returns>
    public static bool Visit(
        string root,
        WalkTuning tuning,
        Action<WalkEntry> onFile,
        Action<WalkEntry> onReparseFile,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Written by the root's own listing alone, before anything below it is queued.
        var rootListed = true;

        Visit(
            root,
            rootState: true,
            tuning,
            (isRoot, contents, descend) =>
            {
                if (isRoot && contents.WasRefused)
                {
                    rootListed = false;
                }

                foreach (var entry in contents.Entries)
                {
                    if (entry.IsDirectory)
                    {
                        descend(entry, false);
                    }
                    else
                    {
                        onFile(entry);
                    }
                }

                foreach (var marked in contents.ReparseFiles)
                {
                    onReparseFile(marked);
                }
            },
            onProgress,
            clock,
            ct);

        return rootListed;
    }

    /// <summary>
    /// Visit every directory at or below <paramref name="root"/>, carrying a caller-chosen value
    /// down the tree with each one.
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
    /// <paramref name="onProgress"/> is called as the other overload calls it.
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        WalkTuning tuning,
        Action<TState, DirectoryContents, Action<WalkEntry, TState>> onDirectory,
        Action onProgress,
        TimeProvider clock,
        CancellationToken ct)
    {
        new WalkWorkers<TState>(tuning, onDirectory, onProgress, clock, ct).Run(LongPath.Extended(root), rootState);
    }
}
