using System.Collections.Concurrent;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What one directory turned out to hold, as the walk found it.
/// </summary>
/// <param name="Entries">
/// The ordinary children — files and directories. A link is not among them; see
/// <paramref name="Links"/> for why they are separated rather than mixed.
/// </param>
/// <param name="Links">
/// The children that are junctions, symbolic links or other name surrogates. Reported separately
/// because a caller totalling bytes must not count them — their target keeps its own place on the
/// volume — while a caller drawing the tree still has to show that they are there. Hiding them
/// outright is what the walk used to do, and it makes a directory the user can see disappear.
/// </param>
/// <param name="WasRefused">
/// Whether the directory could not be listed at all. §5.3 makes that ordinary rather than an error,
/// so the walk still skips it silently — but a caller reporting a total needs to know the total is
/// now a lower bound, and one that never hears about it cannot say so.
/// </param>
internal readonly record struct DirectoryContents(
    IReadOnlyList<FileSystemInfo> Entries,
    IReadOnlyList<DirectoryInfo> Links,
    bool WasRefused);

/// <summary>
/// §5.5's walk, once: breadth-first, bounded parallelism, one level at a time.
///
/// <para>This exists because three callers need the same traversal and differ only in what they do
/// with what it finds — <see cref="ParallelEnumerationScanner"/> adds each file's length,
/// <see cref="HardLinkAwareScanner"/> asks the file whether anything else links it, and
/// <see cref="Exploring.WalkExploreReader"/> records the shape of the tree itself. Written three
/// times, the traversal would carry three copies of two safety rules: §5.3's "access denied is
/// normal, skip silently" and the refusal to follow a reparse point into a tree the caller never
/// classified. A safety rule in three places is one that gets corrected in one of them.</para>
/// </summary>
internal static class BoundedFileWalk
{
    /// <summary>
    /// Visit every file at or below <paramref name="root"/>, minus the two things this walk
    /// deliberately never reaches: anything under a directory it was refused (§5.3), and anything
    /// under a reparse point.
    ///
    /// <paramref name="onFile"/> is called concurrently from several threads, so what it does must
    /// be safe to do that way. <paramref name="onLevel"/> is called once per breadth-first level,
    /// on the walking thread, which is where §5.5's streamed partial totals come from — one report
    /// per level rather than per file, because the UI cannot use thousands of updates a second and
    /// marshalling them would cost more than the enumeration.
    /// </summary>
    /// <param name="root">
    /// The directory to walk, in the extended-length form §6.3 requires. Every path handed to
    /// <paramref name="onFile"/> then carries the prefix too, because .NET builds each child from
    /// the parent it was given.
    /// </param>
    public static void Visit(
        string root,
        Action<FileInfo> onFile,
        Action onLevel,
        CancellationToken ct)
    {
        Visit<byte>(
            root,
            rootState: 0,
            (_, contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    if (entry is DirectoryInfo directory)
                    {
                        descend(directory, 0);
                    }
                    else if (entry is FileInfo file)
                    {
                        onFile(file);
                    }
                }
            },
            onLevel,
            ct);
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
    /// <paramref name="onDirectory"/> is called concurrently. Nothing is descended into unless it
    /// asks: the third argument is how it says so, and a link is never a legitimate argument to it
    /// — the walk holds that rule, not the caller.
    /// </summary>
    public static void Visit<TState>(
        string root,
        TState rootState,
        Action<TState, DirectoryContents, Action<DirectoryInfo, TState>> onDirectory,
        Action onLevel,
        CancellationToken ct)
    {
        var pending = new ConcurrentQueue<(string Path, TState State)>();
        pending.Enqueue((root, rootState));

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
        };

        while (!pending.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<(string Path, TState State)>(pending.Count);
            while (pending.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            Parallel.ForEach(batch, options, item =>
            {
                var contents = Read(item.Path);

                onDirectory(
                    item.State,
                    contents,
                    (directory, state) => pending.Enqueue((directory.FullName, state)));
            });

            onLevel();
        }
    }

    /// <summary>
    /// The immediate children of <paramref name="directory"/>, materialised so an enumeration
    /// failure surfaces here rather than part-way through the caller's accounting.
    ///
    /// Two rules live here and nowhere else. §5.3: a directory we cannot read is skipped rather
    /// than raised, because a locked or refused path is the operating system protecting live state
    /// rather than an error — it is reported as refused so a caller can qualify its total, and
    /// never as an exception. And a reparse point is kept apart from the ordinary children: a
    /// junction's target holds its own place on the volume, so counting through one both
    /// double-counts and describes a tree the caller never classified.
    /// </summary>
    private static DirectoryContents Read(string directory)
    {
        var entries = new List<FileSystemInfo>();
        var links = new List<DirectoryInfo>();

        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(info);
                }
                else if (info is DirectoryInfo link)
                {
                    links.Add(link);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Expected on a live machine. Skip, and say that we did.
            return new DirectoryContents(entries, links, WasRefused: true);
        }

        return new DirectoryContents(entries, links, WasRefused: false);
    }
}
