using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, which cancels <paramref name="cancel"/> as the first file is deleted: a user
/// pressing Cancel with a removal under way. The file itself is still deleted, as it would be, so a
/// removal stopped this way has always taken something.
/// </summary>
/// <param name="whileListing">
/// Cancel as the first directory is listed instead, while a removal is still gathering its tree and
/// has deleted nothing.
/// </param>
public sealed class CancellingFileSystem(
    IFileSystem inner,
    CancellationTokenSource cancel,
    bool whileListing = false) : IFileSystem
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory)
    {
        if (whileListing)
        {
            cancel.Cancel();
        }

        return inner.EnumerateEntries(directory);
    }

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path)
    {
        inner.DeleteFile(path);

        if (!whileListing)
        {
            cancel.Cancel();
        }
    }

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);
}
