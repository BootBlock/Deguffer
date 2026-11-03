using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, calling <c>deleting</c> just before one file is deleted, for a test that has to
/// see what else a removal has done by the time it reaches that file.
/// </summary>
public sealed class DeletionHookFileSystem(IFileSystem inner, string file, Action deleting) : IFileSystem
{
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
        if (LongPath.Display(path).Equals(file, StringComparison.OrdinalIgnoreCase))
        {
            deleting();
        }

        inner.DeleteFile(path);
    }

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    public string? FinalPath(string path) => inner.FinalPath(path);
}
