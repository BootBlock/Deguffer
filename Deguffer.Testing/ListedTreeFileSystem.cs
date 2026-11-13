using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// A filesystem holding only an invented tree, which it lists and records the listing of. Every other
/// question throws, because the walk a removal gathers its tree with lists and does nothing else.
///
/// <para>For a test of what a walk keeps or builds per entry, which needs a tree of thousands of
/// entries at a path of a chosen length, on a volume no machine running the suite has to hold. Each
/// listing hands back entries built on the string it was asked with, as the real listing does, so a
/// test can tell which string the walk listed by.</para>
/// </summary>
public sealed class ListedTreeFileSystem : IFileSystem
{
    private readonly Dictionary<string, List<(string Name, bool IsDirectory)>> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<string> _listed = new();

    /// <param name="root">The tree's top, in any form <see cref="LongPath.Extended"/> accepts.</param>
    public ListedTreeFileSystem(string root) => _folders.Add(Key(root), []);

    /// <summary>Every directory listed, as the string each listing was asked with, in the order asked.</summary>
    public IReadOnlyCollection<string> Listed => _listed;

    /// <summary>Add a folder at <paramref name="path"/>, and every folder above it the tree lacks.</summary>
    public ListedTreeFileSystem AddFolder(string path)
    {
        var key = Key(path);
        if (!_folders.ContainsKey(key))
        {
            _folders.Add(key, []);
            ChildrenOf(key).Add((Path.GetFileName(key), true));
        }

        return this;
    }

    /// <summary>Add an empty file at <paramref name="path"/>, and every folder above it the tree lacks.</summary>
    public ListedTreeFileSystem AddFile(string path)
    {
        var key = Key(path);
        ChildrenOf(key).Add((Path.GetFileName(key), false));

        return this;
    }

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into)
    {
        _listed.Enqueue(directory);

        if (!_folders.TryGetValue(Key(directory), out var children))
        {
            return new(PathPresence.Absent, ListingRefusal.None, WhileReading: false);
        }

        foreach (var (name, isDirectory) in children)
        {
            var attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Archive;
            into.Add(new WalkEntry(directory, request.NameOf(name, isDirectory), attributes, 0, 0, 0));
        }

        return ListingEnd.Listed;
    }

    public PathPresence ProbeDirectory(string path) => throw NotListing();

    public PathPresence ProbeEntry(string path) => throw NotListing();

    public PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink) => throw NotListing();

    public long? TryGetFileLength(string path) => throw NotListing();

    public long? TryGetNewestFileTime(string path) => throw NotListing();

    public void DeleteFile(string path) => throw NotListing();

    public RefusalReason? ProbeRemoval(string path) => throw NotListing();

    public void DeleteDirectory(string path) => throw NotListing();

    public void ClearAttributes(string path) => throw NotListing();

    public FileAttributes? TryGetAttributes(string path) => throw NotListing();

    public bool MayExist(string path) => throw NotListing();

    public string? FinalPath(string path) => throw NotListing();

    private List<(string Name, bool IsDirectory)> ChildrenOf(string key)
    {
        var parent = Path.GetDirectoryName(key)
            ?? throw new ArgumentException($"{key} has no folder above it to sit in.", nameof(key));

        AddFolder(parent);

        return _folders[Key(parent)];
    }

    // Without a trailing separator, so a volume's top, listed as \\?\Q:\, and the folder a path below it
    // is cut to, \\?\Q:, are the same folder.
    private static string Key(string path) => LongPath.Extended(path).TrimEnd(Path.DirectorySeparatorChar);

    private static InvalidOperationException NotListing() =>
        new("An invented tree can only be listed: a walk that gathers asks nothing else of the disk.");
}
