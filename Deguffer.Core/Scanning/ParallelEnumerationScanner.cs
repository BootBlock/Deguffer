using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's fallback: measure a tree by walking it, with a bounded worker pool.
///
/// This is the approach the audit measured at over ten minutes on a handful of profile subtrees,
/// so it is explicitly *not* the scanner — it is what runs where the MFT cannot be read, and every
/// result it produces carries the reason (see <see cref="FallbackReason"/>).
///
/// Its sizes are approximate in one specific way: <c>FileInfo.Length</c> is the logical length, and
/// nothing here can see how many clusters a compressed or sparse file actually occupies. Learning
/// that would cost a <c>GetCompressedFileSize</c> call per file, on the path that is already the
/// slow one. <see cref="ScanSize.Approximate"/> records the compromise rather than hiding it.
/// </summary>
public sealed class ParallelEnumerationScanner : IDirectoryScanner
{
    /// <summary>
    /// One instance per reason, built once. G5: this type is stateless apart from the reason it
    /// stamps on results, and <see cref="Because"/> sits on the fallback path — which §6.3 makes
    /// the *ordinary* path — so constructing one per measurement would allocate per directory
    /// scanned for no benefit.
    /// </summary>
    private static readonly ParallelEnumerationScanner[] ByReason =
        [.. Enum.GetValues<FallbackReason>().Order().Select(r => new ParallelEnumerationScanner(r))];

    public static readonly ParallelEnumerationScanner Default = ByReason[(int)FallbackReason.None];

    private readonly FallbackReason _reason;

    private ParallelEnumerationScanner(FallbackReason reason) => _reason = reason;

    /// <summary>Same walk, attributed to whichever reason sent the caller here.</summary>
    public ParallelEnumerationScanner Because(FallbackReason reason) => ByReason[(int)reason];

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(() => ScanResult.Slow(Measure(path, progress, ct), _reason), ct));

    /// <summary>
    /// Always null: this scanner holds no index, so it has nothing to search. Answering by walking
    /// here would hide the walk behind the accelerator's signature, and §5.5 requires the slow
    /// route to be visible to the caller that takes it.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    private static ScanSize Measure(string path, IProgress<ScanSize>? progress, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(path))
        {
            return ScanSize.Approximate(0);
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

            // §5.5: stream partial results. One report per breadth-first level, not per file —
            // the UI cannot use thousands of updates a second, and marshalling them would cost
            // more than the enumeration.
            progress?.Report(ScanSize.Approximate(Interlocked.Read(ref total)));
        }

        return ScanSize.Approximate(Interlocked.Read(ref total));
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
