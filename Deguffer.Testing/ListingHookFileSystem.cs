using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, calling <c>listed</c> just before each listing of one directory, for a test
/// that changes the disk at that moment: after a walk decided what to list, and before it is listed.
/// </summary>
public sealed class ListingHookFileSystem(IFileSystem inner, string directory, Action listed) : IFileSystem
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string path, ListingRequest request, List<WalkEntry> into)
    {
        if (LongPath.Display(path).Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            listed();
        }

        return inner.ListEntries(path, request, into);
    }

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);
}
