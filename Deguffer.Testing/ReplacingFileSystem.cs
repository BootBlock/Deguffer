using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, except that a file takes the name of <paramref name="replaced"/> the moment
/// the directory there is removed: another program writing a same-named file while a removal runs.
/// The race cannot be staged against a real disk, and a removal's claim that its root went has to
/// mean nothing is there, not only that no directory is.
/// </summary>
/// <param name="replaced">The directory to replace, in the extended form a removal asks in (§6.3).</param>
public sealed class ReplacingFileSystem(IFileSystem inner, string replaced) : IFileSystem
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        inner.ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path)
    {
        inner.DeleteDirectory(path);

        if (string.Equals(path, replaced, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(path, new byte[16]);
        }
    }

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);
}
