using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The pass that removes a tree's folders once its files are gone: in parallel, never a folder before
/// the folders inside it, and with an exact record of every folder it did not take.
/// </summary>
public sealed class DirectoryRemoverFolderPassTests : IDisposable
{
    private const int Branches = 4;
    private const int Leaves = 4;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The first folder in each branch is removed sooner than its siblings, so a pass that tried a
    /// parent as soon as one child was done would find the others still there.
    /// </summary>
    [Fact]
    public async Task TriesNoFolderBeforeEveryFolderInsideIt()
    {
        var (root, folders) = CreateTree();
        var neighbour = _temp.CreateFile(64, "neighbour", "keep.bin");
        var watched = new WatchedDirectoryRemovals(
            WindowsFileSystem.Default,
            hold: path => TimeSpan.FromMilliseconds(path.EndsWith(@"\l0", StringComparison.Ordinal) ? 1 : 40));

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, default, watched);

        Assert.Empty(watched.TriedEarly);
        Assert.True(outcome.RootRemoved);
        Assert.Empty(outcome.LeftStanding);
        Assert.All(folders, folder => Assert.False(Directory.Exists(folder), folder));

        // §5.6: the folder beside the root was never the removal's to take.
        Assert.True(File.Exists(neighbour), "the removal reached outside its root");
    }

    /// <summary>
    /// Sibling folders are removed at the same time rather than one after another, which on a tree of a
    /// hundred thousand folders is the difference between a pass that keeps pace with the files and one
    /// that takes longer than they did.
    /// </summary>
    [Fact]
    public async Task RemovesSiblingFoldersAtOnce()
    {
        var (root, _) = CreateTree();
        var watched = new WatchedDirectoryRemovals(WindowsFileSystem.Default);

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, default, watched);

        Assert.True(outcome.RootRemoved);
        Assert.True(watched.MostAtOnce > 1, $"at most {watched.MostAtOnce} folder removal ran at once");
    }

    /// <summary>
    /// A removal stopped while it is removing folders names exactly the folders still there: every one
    /// it never tried and every one it tried that stayed, and none it took. A folder that is gone but
    /// named would read to §5.6 as somewhere the removal left, and one still there but unnamed as a
    /// protected folder it never entered.
    /// </summary>
    [Fact]
    public async Task AFolderPassStoppedPartWayNamesExactlyTheFoldersStillThere()
    {
        var (root, folders) = CreateTree();
        using var cts = new CancellationTokenSource();
        var begun = 0;
        var watched = new WatchedDirectoryRemovals(
            WindowsFileSystem.Default,
            removing: () =>
            {
                if (Interlocked.Increment(ref begun) == 6)
                {
                    cts.Cancel();
                }
            });

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, cts.Token, watched);

        var standing = folders.Where(Directory.Exists).ToList();

        Assert.True(outcome.Interrupted);
        Assert.False(outcome.RootRemoved);
        Assert.Contains(root, standing);
        Assert.InRange(standing.Count, 1, folders.Count - 1);
        Assert.Equal(
            standing.Order(StringComparer.OrdinalIgnoreCase),
            outcome.LeftStanding.Order(StringComparer.OrdinalIgnoreCase));

        // Every file went before the folder pass began, and each folder taken is one entry more.
        Assert.Equal(Branches * Leaves + (folders.Count - standing.Count), outcome.EntriesRemoved);
        Assert.True(outcome.RefusedFolders.IsEmpty);
    }

    /// <summary>
    /// A root with <see cref="Branches"/> folders, each holding <see cref="Leaves"/> folders with a file
    /// in each.
    /// </summary>
    /// <returns>The root, and every folder in the tree, the root included.</returns>
    private (string Root, IReadOnlyList<string> Folders) CreateTree()
    {
        var root = _temp.CreateDirectory("cache");
        var folders = new List<string> { root };

        for (var branch = 0; branch < Branches; branch++)
        {
            folders.Add(_temp.CreateDirectory("cache", $"b{branch}"));

            for (var leaf = 0; leaf < Leaves; leaf++)
            {
                folders.Add(_temp.CreateDirectory("cache", $"b{branch}", $"l{leaf}"));
                _temp.CreateFile(16, "cache", $"b{branch}", $"l{leaf}", "f.bin");
            }
        }

        return (root, folders);
    }
}
