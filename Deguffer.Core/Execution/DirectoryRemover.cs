using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <param name="BytesReclaimed">Bytes of files actually deleted.</param>
/// <param name="Skipped">Entries left in place because something held them (§5.3).</param>
/// <param name="RootRemoved">Whether the target directory itself is gone.</param>
public sealed record RemovalOutcome(long BytesReclaimed, int Skipped, bool RootRemoved);

/// <summary>
/// Deletes a directory tree.
///
/// §6.3: deletion is genuinely parallel — these trees are hundreds of thousands of small files,
/// and wall-clock time is dominated by per-file overhead, not bytes. Every path goes through the
/// extended-length prefix, because a MAX_PATH truncation here is a silent partial deletion.
/// </summary>
public static class DirectoryRemover
{
    public static Task<RemovalOutcome> RemoveAsync(
        string path,
        IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => Remove(path, progress, ct), ct);

    private static RemovalOutcome Remove(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var extended = LongPath.Extended(path);

        if (!Directory.Exists(extended))
        {
            return new RemovalOutcome(0, 0, RootRemoved: true);
        }

        // Two passes: gather the tree first so progress is a real fraction rather than a guess,
        // then delete depth-first. Gathering also means a mid-run enumeration failure cannot
        // leave us deleting a partially-understood tree.
        var directories = new List<string>();
        var files = new List<(string Path, long Length)>();
        Gather(extended, directories, files, ct);

        long reclaimed = 0;
        var skipped = 0;
        var done = 0;
        var total = Math.Max(files.Count, 1);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount * 4, 32),
        };

        Parallel.ForEach(files, options, file =>
        {
            if (TryDeleteFile(file.Path))
            {
                Interlocked.Add(ref reclaimed, file.Length);
            }
            else
            {
                Interlocked.Increment(ref skipped);
            }

            var completed = Interlocked.Increment(ref done);
            if (completed % 256 == 0 || completed == files.Count)
            {
                progress?.Report((double)completed / total);
            }
        });

        // Deepest first, so a directory is only removed once its children are gone. Ordering by
        // path length is a correct topological order here, not a shortcut: a parent's path is
        // always a strict prefix of its descendants', so it is always strictly shorter.
        // Directories still holding a skipped file simply stay — the correct outcome, not an error.
        foreach (var directory in directories.OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();
            TryDeleteDirectory(directory);
        }

        progress?.Report(1.0);

        // The root is in `directories`, so the loop above has already attempted it.
        return new RemovalOutcome(reclaimed, skipped, RootRemoved: !Directory.Exists(extended));
    }

    private static void Gather(
        string extendedDirectory,
        List<string> directories,
        List<(string, long)> files,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        directories.Add(extendedDirectory);

        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(extendedDirectory).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // Unreadable directory: nothing to gather, and §5.3 says skip rather than fail.
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Never follow a junction or symlink: deletion would escape the target tree.
                // Remove the link itself and stop there.
                if (entry is DirectoryInfo)
                {
                    TryDeleteDirectory(entry.FullName);
                }
                else
                {
                    TryDeleteFile(entry.FullName);
                }

                continue;
            }

            if (entry is FileInfo file)
            {
                files.Add((file.FullName, file.Length));
            }
            else
            {
                Gather(entry.FullName, directories, files, ct);
            }
        }
    }

    private static bool TryDeleteFile(string extendedPath)
    {
        try
        {
            File.Delete(extendedPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Commonly just the read-only bit — package manager caches set it liberally.
            try
            {
                File.SetAttributes(extendedPath, FileAttributes.Normal);
                File.Delete(extendedPath);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            // Held open by a live process. §5.3: this is the OS protecting state; skip it.
            return false;
        }
    }

    private static void TryDeleteDirectory(string extendedPath)
    {
        try
        {
            Directory.Delete(extendedPath, recursive: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Not empty, or in use. Leave it.
        }
    }
}
