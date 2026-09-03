using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The real filesystem, except that one directory refuses to be listed.
///
/// <para>A refusal is ordinary (§5.3) and a listing right is separate from a traverse right, so a
/// folder can be undeletable to enumerate and still hold a child this account may remove. That
/// combination is what leaves §5.6 with nothing to compare against, and building it by hand is the
/// only way to reach that branch without an access rule on a real directory.</para>
/// </summary>
public sealed class UnlistableFileSystem(IFileSystem inner, string refused) : IFileSystem
{
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) =>
        LongPath.Display(directory).Equals(refused, StringComparison.OrdinalIgnoreCase)
            ? throw new UnauthorizedAccessException($"Refused: {directory}")
            : inner.EnumerateEntries(directory);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);
}
