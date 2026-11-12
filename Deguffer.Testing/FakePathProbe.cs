using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Files, folders, links, refusals and unplugged drives a test describes. Every drive root answers
/// unless a test disconnects it, and every other path is absent, as on a machine that has nothing
/// there.
///
/// <para>Safe to ask from several threads at once, as a caller that probes in parallel does
/// (<c>BrokenLinks.Find</c> through <c>LinkTargets</c>). What a test describes is set before it asks,
/// so only the counts change while it runs.</para>
/// </summary>
public sealed class FakePathProbe : IPathProbe
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times each path was asked about as a file, for a test that counts.</summary>
    public ConcurrentDictionary<string, int> FileQueries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times each path was asked about as either kind.</summary>
    public ConcurrentDictionary<string, int> EntryQueries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times each path was asked about as a directory.</summary>
    public ConcurrentDictionary<string, int> DirectoryQueries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakePathProbe File(string path)
    {
        _files.Add(path);
        return this;
    }

    public FakePathProbe Directory(string path, bool isLink = false)
    {
        _directories[path] = isLink;
        return this;
    }

    /// <summary>Take away a file or a directory, as an uninstaller or a user would.</summary>
    public void Remove(string path)
    {
        _files.Remove(path);
        _directories.Remove(path);
    }

    /// <summary>A path Windows will not describe, as either kind.</summary>
    public FakePathProbe Refused(string path)
    {
        _refused.Add(path);
        return this;
    }

    /// <summary>A drive or share that is not connected: its root, and so everything on it, reads absent.</summary>
    public FakePathProbe Disconnected(string root)
    {
        _disconnected.Add(root);
        return this;
    }

    public PathPresence ProbeFile(string path) =>
        Ask(FileQueries, path) ? PathPresence.Refused : _files.Contains(path) ? PathPresence.Present : PathPresence.Absent;

    public PathPresence ProbeEntry(string path) =>
        Ask(EntryQueries, path) ? PathPresence.Refused
        : _files.Contains(path) || IsDirectory(path) ? PathPresence.Present
        : PathPresence.Absent;

    public PathPresence ProbeDirectory(string path, out bool? isLink)
    {
        isLink = null;

        if (Ask(DirectoryQueries, path))
        {
            return PathPresence.Refused;
        }

        if (!IsDirectory(path))
        {
            return PathPresence.Absent;
        }

        isLink = _directories.GetValueOrDefault(path);
        return PathPresence.Present;
    }

    /// <summary>Count the question, and answer whether Windows refuses it.</summary>
    private bool Ask(ConcurrentDictionary<string, int> queries, string path)
    {
        queries.AddOrUpdate(path, 1, static (_, count) => count + 1);
        return _refused.Contains(path);
    }

    private bool IsDirectory(string path) =>
        _directories.ContainsKey(path)
        || (Path.GetPathRoot(path) is { Length: > 0 } root
            && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)
            && !_disconnected.Contains(root));
}
