using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, with each deletion of a file or a directory held for a moment so that
/// deletions able to overlap do, and counted: the most that were under way at the same moment.
/// </summary>
/// <param name="hold">How long each deletion is held before it is made.</param>
public sealed class WatchedDeletions(IFileSystem inner, TimeSpan hold) : IFileSystem
{
    private readonly Lock _gate = new();
    private int _running;
    private int _mostAtOnce;

    /// <summary>The most deletions, files and directories together, under way at the same moment.</summary>
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

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        inner.ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => Held(() => inner.DeleteFile(path));

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => Held(() => inner.DeleteDirectory(path));

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    public string? FinalPath(string path) => inner.FinalPath(path);

    private void Held(Action delete)
    {
        lock (_gate)
        {
            _mostAtOnce = Math.Max(_mostAtOnce, ++_running);
        }

        try
        {
            Thread.Sleep(hold);
            delete();
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }
}
