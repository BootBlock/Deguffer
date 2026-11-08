using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// A filesystem with nothing on it: every path is absent, and Windows answers every question about
/// one as it answers for a path nothing is at.
///
/// <para>For a test whose paths are invented, on a fake volume at a letter the machine may use, such
/// as a tree an Explore scan was handed rather than one it read. The policy asks the disk where a
/// folder above an item leads and what a refused location holds, and on the real filesystem that
/// question reaches whatever the machine running the suite keeps at the letter, so its answer could
/// decide the verdict. Here no reading can find anything.</para>
/// </summary>
public sealed class EmptyFileSystem : IFileSystem
{
    public static EmptyFileSystem Instance { get; } = new();

    private EmptyFileSystem()
    {
    }

    public PathPresence ProbeDirectory(string path) => PathPresence.Absent;

    public PathPresence ProbeEntry(string path) => PathPresence.Absent;

    public PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink)
    {
        (isDirectory, isLink) = (null, null);

        return PathPresence.Absent;
    }

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        new(PathPresence.Absent, ListingRefusal.None, WhileReading: false);

    public long? TryGetFileLength(string path) => null;

    public long? TryGetNewestFileTime(string path) => null;

    /// <summary>
    /// Refused as <see cref="File.Delete"/> refuses a file whose folder is not there either, which on
    /// a filesystem with nothing on it is every file.
    /// </summary>
    public void DeleteFile(string path) => throw NotFound(path);

    public RefusalReason? ProbeRemoval(string path) => null;

    public void DeleteDirectory(string path) => throw NotFound(path);

    public void ClearAttributes(string path) => throw NotFound(path);

    private static DirectoryNotFoundException NotFound(string path) =>
        new($"Could not find a part of the path '{path}'.");

    public FileAttributes? TryGetAttributes(string path) => null;

    public bool MayExist(string path) => false;

    public string? FinalPath(string path) => null;
}
