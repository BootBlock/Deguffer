using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's walk, once: breadth-first, bounded parallelism, one level at a time.
///
/// <para>This exists because two scanners need the same traversal and differ only in what they do
/// with each file — <see cref="ParallelEnumerationScanner"/> adds its length,
/// <see cref="HardLinkAwareScanner"/> asks the file whether anything else links it. Written twice,
/// the traversal carried two copies of two safety rules: §5.3's "access denied is normal, skip
/// silently" and the refusal to follow a reparse point into a tree the caller never classified. A
/// safety rule in two places is one that gets corrected in one of them.</para>
/// </summary>
internal static class BoundedFileWalk
{
    /// <summary>
    /// Visit every file at or below <paramref name="root"/>, minus the two things this walk
    /// deliberately never reaches: anything under a directory it was refused (§5.3), and anything
    /// under a reparse point. Both are described on <see cref="EnumerateSafely"/>, which is where
    /// they are applied.
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
        var pending = new ConcurrentQueue<string>();
        pending.Enqueue(root);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 2, 16),
        };

        while (!pending.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<string>(pending.Count);
            while (pending.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            Parallel.ForEach(batch, options, directory =>
            {
                foreach (var entry in EnumerateSafely(directory))
                {
                    if (entry is DirectoryInfo)
                    {
                        pending.Enqueue(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        onFile(file);
                    }
                }
            });

            onLevel();
        }
    }

    /// <summary>
    /// The immediate children of <paramref name="directory"/>, materialised so an enumeration
    /// failure surfaces here rather than part-way through the caller's accounting.
    ///
    /// Two rules live here and nowhere else. §5.3: a directory we cannot read is skipped silently,
    /// because a locked or refused path is the operating system protecting live state rather than
    /// an error. And a reparse point is never followed: a junction's target keeps its own place on
    /// the volume, so walking through one both double-counts and describes a tree the caller never
    /// classified.
    /// </summary>
    private static List<FileSystemInfo> EnumerateSafely(string directory)
    {
        var entries = new List<FileSystemInfo>();

        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(info);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Expected on a live machine. Skip.
        }

        return entries;
    }
}
