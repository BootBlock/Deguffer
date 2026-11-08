using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// Passes every call through to the real filesystem, recording the path it was given.
///
/// A decorator rather than a fake tree on purpose: the point is to observe what a genuine removal
/// hands to Win32, so the behaviour under test stays real and only the paths are inspected.
/// Deletion is parallel (§6.3), so recording has to be thread-safe.
/// </summary>
public sealed class RecordingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentQueue<string> _paths = new();
    private readonly ConcurrentQueue<string> _probed = new();
    private readonly ConcurrentQueue<string> _resolved = new();
    private readonly ConcurrentQueue<string> _listed = new();
    private readonly ConcurrentQueue<string> _described = new();

    public IReadOnlyCollection<string> Paths => _paths;

    /// <summary>
    /// The paths opened for deletion without deleting them. Kept apart from <see cref="Paths"/>,
    /// which holds them too, because that open is the one call here with a cost to another program
    /// — see <c>DeletionProbe</c> — and a test has to be able to say where it happened.
    /// </summary>
    public IReadOnlyCollection<string> Probed => _probed;

    /// <summary>
    /// The paths asked where they really lead, which <see cref="Paths"/> holds too. Kept apart so a
    /// test counting how often an entry is described is not counting the question of where it is.
    /// </summary>
    public IReadOnlyCollection<string> Resolved => _resolved;

    /// <summary>
    /// The directories listed, once for each listing, which <see cref="Paths"/> holds too. Kept apart
    /// so a test can count how many times a tree was walked.
    /// </summary>
    public IReadOnlyCollection<string> Listed => _listed;

    /// <summary>
    /// The paths a probe asked what is there, once for each probe, which <see cref="Paths"/> holds too.
    /// Kept apart so a test can say a path was described once: two reads of one path can describe
    /// two entries.
    /// </summary>
    public IReadOnlyCollection<string> Described => _described;

    public PathPresence ProbeDirectory(string path)
    {
        _paths.Enqueue(path);
        _described.Enqueue(path);
        return inner.ProbeDirectory(path);
    }

    public PathPresence ProbeEntry(string path)
    {
        _paths.Enqueue(path);
        _described.Enqueue(path);
        return inner.ProbeEntry(path);
    }

    public PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink)
    {
        _paths.Enqueue(path);
        _described.Enqueue(path);
        return inner.ProbeEntry(path, out isDirectory, out isLink);
    }

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into)
    {
        _paths.Enqueue(directory);
        _listed.Enqueue(directory);
        return inner.ListEntries(directory, request, into);
    }

    public long? TryGetNewestFileTime(string path)
    {
        _paths.Enqueue(path);
        return inner.TryGetNewestFileTime(path);
    }

    public long? TryGetFileLength(string path)
    {
        _paths.Enqueue(path);
        return inner.TryGetFileLength(path);
    }

    public void DeleteFile(string path)
    {
        _paths.Enqueue(path);
        inner.DeleteFile(path);
    }

    public RefusalReason? ProbeRemoval(string path)
    {
        _paths.Enqueue(path);
        _probed.Enqueue(path);
        return inner.ProbeRemoval(path);
    }

    public void DeleteDirectory(string path)
    {
        _paths.Enqueue(path);
        inner.DeleteDirectory(path);
    }

    public void ClearAttributes(string path)
    {
        _paths.Enqueue(path);
        inner.ClearAttributes(path);
    }

    public FileAttributes? TryGetAttributes(string path)
    {
        _paths.Enqueue(path);
        return inner.TryGetAttributes(path);
    }

    public bool MayExist(string path)
    {
        _paths.Enqueue(path);
        return inner.MayExist(path);
    }

    public string? FinalPath(string path)
    {
        _paths.Enqueue(path);
        _resolved.Enqueue(path);
        return inner.FinalPath(path);
    }
}
