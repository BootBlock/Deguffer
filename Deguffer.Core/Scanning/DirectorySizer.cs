using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Measures a directory tree with a bounded parallel worker pool.
///
/// §5.5 notes that naive recursive enumeration is too slow to be *the scanner* — full-drive
/// discovery needs the MFT, which is Milestone 2. Milestone 1 only measures known provider
/// paths, which is targeted enough that parallel enumeration is the right tool: these trees are
/// hundreds of thousands of small files, so wall-clock is dominated by per-entry overhead.
/// </summary>
public static class DirectorySizer
{
    public static Task<long> MeasureAsync(string path, CancellationToken ct = default) =>
        Task.Run(() => Measure(path, ct), ct);

    private static long Measure(string path, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(path))
        {
            return 0;
        }

        long total = 0;
        var pending = new ConcurrentQueue<string>();
        pending.Enqueue(LongPath.Extended(path));

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
                    if (entry.IsDirectory)
                    {
                        pending.Enqueue(entry.Path);
                    }
                    else
                    {
                        Interlocked.Add(ref total, entry.Length);
                    }
                }
            });
        }

        return total;
    }

    private readonly record struct Entry(string Path, bool IsDirectory, long Length);

    /// <summary>
    /// §5.3: treat access denied as normal and skip silently. A directory we cannot read is
    /// simply not counted; it is never a reason to abandon the scan.
    /// </summary>
    private static List<Entry> EnumerateSafely(string extendedDirectory)
    {
        var entries = new List<Entry>();

        try
        {
            foreach (var info in new DirectoryInfo(extendedDirectory).EnumerateFileSystemInfos())
            {
                // Reparse points are followed nowhere: a junction into another tree would both
                // double-count and, worse, make deletion escape the target.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                entries.Add(info is FileInfo file
                    ? new Entry(file.FullName, IsDirectory: false, file.Length)
                    : new Entry(info.FullName, IsDirectory: true, 0));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Expected on a live machine. Skip.
        }

        return entries;
    }
}
