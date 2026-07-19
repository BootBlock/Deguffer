using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Passes every call through to the real filesystem, recording the path it was given.
///
/// A decorator rather than a fake tree on purpose: the point is to observe what a genuine removal
/// hands to Win32, so the behaviour under test stays real and only the paths are inspected.
/// Deletion is parallel (§6.3), so recording has to be thread-safe.
/// </summary>
public sealed class RecordingFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly ConcurrentQueue<string> _paths = new();

    public IReadOnlyCollection<string> Paths => _paths;

    public bool DirectoryExists(string path)
    {
        _paths.Enqueue(path);
        return inner.DirectoryExists(path);
    }

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string directory)
    {
        _paths.Enqueue(directory);
        return inner.EnumerateEntries(directory);
    }

    public void DeleteFile(string path)
    {
        _paths.Enqueue(path);
        inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path)
    {
        _paths.Enqueue(path);
        inner.DeleteDirectory(path);
    }

    public void ClearAttributes(string path)
    {
        _paths.Enqueue(path);
        inner.ClearAttributes(path);
    }
}
