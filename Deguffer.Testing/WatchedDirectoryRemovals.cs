using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, with each directory removal held for a moment so that removals able to overlap
/// do, and watched: how many ran at once, and which began while a folder inside was still there.
/// </summary>
/// <param name="hold">
/// How long to hold the removal of a directory, by its path. 15 ms for each by default. A folder held
/// for less than its siblings is finished first, which is when a pass that tried a parent early would
/// find a sibling still there.
/// </param>
/// <param name="removing">Called as each directory removal begins, before it is held.</param>
public sealed class WatchedDirectoryRemovals(
    IFileSystem inner,
    Func<string, TimeSpan>? hold = null,
    Action? removing = null) : IFileSystem
{
    private static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(15);

    private readonly ConcurrentQueue<string> _triedEarly = new();
    private readonly Lock _gate = new();
    private int _running;
    private int _mostAtOnce;

    /// <summary>The most directory removals that were under way at the same moment.</summary>
    public int MostAtOnce
    {
        get
        {
            lock (_gate)
            {
                return _mostAtOnce;
            }
        }
    }

    /// <summary>Each directory whose removal began while a directory inside it was still there.</summary>
    public IReadOnlyCollection<string> TriedEarly => _triedEarly;

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, EnumerationOptions options, FileNames files, List<WalkEntry> into) =>
        inner.ListEntries(directory, options, files, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path)
    {
        removing?.Invoke();

        // A child still there when its parent is tried was not finished first, whether nobody had
        // tried it yet or its removal was still under way.
        var entries = new List<WalkEntry>();
        inner.ListEntries(path, DirectoryListing.EveryEntry, FileNames.None, entries);

        if (entries.Any(entry => entry.IsDirectory))
        {
            _triedEarly.Enqueue(path);
        }

        lock (_gate)
        {
            _mostAtOnce = Math.Max(_mostAtOnce, ++_running);
        }

        try
        {
            Thread.Sleep(hold?.Invoke(path) ?? DefaultHold);
            inner.DeleteDirectory(path);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);
}
