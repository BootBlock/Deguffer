using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The real filesystem, except that some files refuse to be deleted, each for the reason it is
/// given.
///
/// <para>Built by hand for what the real refusals cannot give a test: a chosen reason for each of
/// several files at chosen depths in one tree, answered the same way by the deletion and by the open
/// for deletion. A held <see cref="FileStream"/> and <see cref="UndeletableFile"/> are Windows'
/// own refusals, for the tests that need Windows itself to say no; this is for the tests about what
/// the removal and the check do with the answer.</para>
///
/// <para>A refused file refuses the attribute reset as well when it is denied, because that is what
/// Windows does to a file this account may not touch: the removal's read-only retry must meet the
/// same answer twice, or the classification it makes from the second one goes untested.</para>
/// </summary>
public sealed class RefusingFileSystem(IFileSystem inner, IReadOnlyDictionary<string, RefusalReason> refused) : IFileSystem
{
    private const int SharingViolation = unchecked((int)0x80070020);

    private readonly Dictionary<string, RefusalReason> _refused =
        new(refused, StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory) => inner.EnumerateEntries(directory);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path)
    {
        switch (ReasonFor(path))
        {
            case RefusalReason.InUse:
                throw new IOException($"In use: {path}", SharingViolation);
            case RefusalReason.Denied:
                throw new UnauthorizedAccessException($"Denied: {path}");
            default:
                inner.DeleteFile(path);
                break;
        }
    }

    public RefusalReason? ProbeRemoval(string path) => ReasonFor(path) ?? inner.ProbeRemoval(path);

    /// <summary>
    /// A listed directory refuses too, in the form Windows gives each reason for a folder: a sharing
    /// violation where something is using it, and a bare <see cref="IOException"/> with no Win32 error
    /// where it is denied. The second is not <see cref="UnauthorizedAccessException"/>, which is what
    /// the same denial of a file throws.
    /// </summary>
    public void DeleteDirectory(string path)
    {
        switch (ReasonFor(path))
        {
            case RefusalReason.InUse:
                throw new IOException($"In use: {path}", SharingViolation);
            case RefusalReason.Denied:
                throw new IOException($"Access to the path '{path}' is denied.");
            default:
                inner.DeleteDirectory(path);
                break;
        }
    }

    public void ClearAttributes(string path)
    {
        if (ReasonFor(path) == RefusalReason.Denied)
        {
            throw new UnauthorizedAccessException($"Denied: {path}");
        }

        inner.ClearAttributes(path);
    }

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    private RefusalReason? ReasonFor(string path) =>
        _refused.TryGetValue(LongPath.Display(path), out var reason) ? reason : null;
}
