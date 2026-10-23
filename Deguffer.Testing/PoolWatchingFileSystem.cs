using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, counting each deletion and each open for deletion, and how many of them ran
/// on a thread-pool thread: how a test pins a removal's or a refusal check's per-file work off the
/// pool.
///
/// <para>Each counted call is held for <c>hold</c> first, as a disk would hold it, so that a loop on
/// the pool hands some of its items to pool threads rather than finishing them all on the thread that
/// started it.</para>
/// </summary>
public sealed class PoolWatchingFileSystem(IFileSystem inner, TimeSpan hold) : IFileSystem
{
    private int _calls;
    private int _onThePool;

    /// <summary>How many deletions and opens for deletion were made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many of <see cref="Calls"/> ran on a thread-pool thread.</summary>
    public int OnThePool => Volatile.Read(ref _onThePool);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        inner.ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path)
    {
        Watch();
        inner.DeleteFile(path);
    }

    public RefusalReason? ProbeRemoval(string path)
    {
        Watch();
        return inner.ProbeRemoval(path);
    }

    public void DeleteDirectory(string path)
    {
        Watch();
        inner.DeleteDirectory(path);
    }

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    private void Watch()
    {
        Interlocked.Increment(ref _calls);

        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            Interlocked.Increment(ref _onThePool);
        }

        Thread.Sleep(hold);
    }
}
