using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Testing;

/// <summary>
/// The real filesystem, calling a tree's hook on the thread about to make the first deletion inside
/// that tree, once, before the deletion is made. For a test that proves by order what removals of
/// several trees do at once: a hook that waits for another tree's hook can be reached only if both
/// removals are under way.
/// </summary>
/// <param name="hooks">Each tree, as a full path in display form, and what to call before its first deletion.</param>
public sealed class FirstDeletionHooks(IFileSystem inner, IReadOnlyDictionary<string, Action> hooks) : IFileSystem
{
    private readonly (string Tree, Action Hook)[] _trees = [.. hooks.Select(pair => (pair.Key, pair.Value))];
    private readonly int[] _called = new int[hooks.Count];
    private readonly ConcurrentQueue<string> _deleted = new();

    /// <summary>Every path a deletion was asked for, in display form, in the order the deletions began.</summary>
    public IReadOnlyCollection<string> Deleted => _deleted;

    public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

    public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

    public PathPresence ProbeEntry(string path, out bool? isDirectory, out bool? isLink) =>
        inner.ProbeEntry(path, out isDirectory, out isLink);

    public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into) =>
        inner.ListEntries(directory, request, into);

    public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

    public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

    public void DeleteFile(string path)
    {
        Deleting(path);
        inner.DeleteFile(path);
    }

    public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

    public void DeleteDirectory(string path)
    {
        Deleting(path);
        inner.DeleteDirectory(path);
    }

    public void ClearAttributes(string path) => inner.ClearAttributes(path);

    public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

    public bool MayExist(string path) => inner.MayExist(path);

    public string? FinalPath(string path) => inner.FinalPath(path);

    private void Deleting(string path)
    {
        var shown = LongPath.Display(path);
        _deleted.Enqueue(shown);

        for (var i = 0; i < _trees.Length; i++)
        {
            if (LongPath.Contains(_trees[i].Tree, shown) && Interlocked.Exchange(ref _called[i], 1) == 0)
            {
                _trees[i].Hook();
            }
        }
    }
}
