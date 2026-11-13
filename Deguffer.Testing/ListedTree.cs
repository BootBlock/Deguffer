using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// An invented tree, listed through <see cref="List"/> and recording each listing: what a walk that
/// only lists, such as the one a removal gathers its tree with, can be handed in place of the disk.
///
/// <para>For a test of what a walk keeps or builds per entry, which needs a tree of thousands of
/// entries at a path of a chosen length, on a volume no machine running the suite has to hold. Each
/// listing hands back entries built on the string it was asked with, as the real listing does, so a
/// test can tell which string the walk listed by.</para>
/// </summary>
public sealed class ListedTree
{
    private readonly Dictionary<string, List<(string Name, bool IsDirectory)>> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<string> _listed = new();

    /// <param name="root">The tree's top, in any form <see cref="LongPath.Extended"/> accepts.</param>
    public ListedTree(string root) => _folders.Add(Key(root), []);

    /// <summary>Every directory listed, as the string each listing was asked with, in the order asked.</summary>
    public IReadOnlyCollection<string> Listed => _listed;

    /// <summary>Add a folder at <paramref name="path"/>, and every folder above it the tree lacks.</summary>
    public ListedTree AddFolder(string path)
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
    public ListedTree AddFile(string path)
    {
        var key = Key(path);
        ChildrenOf(key).Add((Path.GetFileName(key), false));

        return this;
    }

    /// <summary>List <paramref name="directory"/> into <paramref name="into"/>, as a <see cref="ListDirectory"/>.</summary>
    public ListingEnd List(string directory, ListingRequest request, List<WalkEntry> into)
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
}
