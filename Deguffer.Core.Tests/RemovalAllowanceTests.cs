using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every deletion a removal makes takes a place in the allowance the steps beside it share, whichever
/// route makes it: a tree's crew, a link removed as itself, and a single file. With every place taken
/// by another removal, none deletes anything, and each stops as a cancelled removal does.
/// </summary>
public sealed class RemovalAllowanceTests : IDisposable
{
    /// <summary>
    /// How long a removal is given to delete something it must not, before the clean is cancelled. It
    /// proves nothing on its own: a removal that waits for its place passes however long it is given.
    /// </summary>
    private static readonly TimeSpan Chance = TimeSpan.FromMilliseconds(300);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ATreeIsNotDeletedWhileEveryPlaceIsTaken()
    {
        var tree = _temp.CreateDirectory("cache");
        var files = Enumerable.Range(0, 50).Select(i => _temp.CreateFile(16, "cache", $"d{i % 4}", $"f{i}.bin")).ToList();
        var allowance = Full();
        using var cts = new CancellationTokenSource(Chance);

        var removal = await DirectoryRemover.RemoveAsync(tree, ct: cts.Token, deletions: allowance);

        Assert.True(removal.Interrupted);
        Assert.Equal(0, removal.EntriesRemoved);
        Assert.All(files, file => Assert.True(File.Exists(file), $"{file} was deleted without a place"));

        // Once the place is given back, the same removal takes the tree.
        allowance.Return();
        var after = await DirectoryRemover.RemoveAsync(tree, deletions: allowance);

        Assert.True(after.RootRemoved);
        Assert.True(allowance.TryTake(), "The removal kept a place after it ended.");
    }

    /// <summary>
    /// A removal that started while every place was taken, as a large tree does behind small ones, takes
    /// each place as it is given back rather than deleting with one thread to its end. The second place
    /// comes back during the removal's first deletion, which is held until a second deletion begins.
    ///
    /// <para>Both places are taken while the tree is walked, so no helper starts then. The first comes
    /// back as the walk ends, before the walk's thread asks for one, and the second during the first
    /// deletion. Whichever thread makes that deletion, the other can only begin the second if the place
    /// given back was offered to the removal: the walk's thread, once it holds a place, asks for no
    /// other.</para>
    /// </summary>
    [Fact]
    public void ARemovalTakesAPlaceTheMomentOneIsGivenBack()
    {
        var tree = _temp.CreateDirectory("cache");
        for (var i = 0; i < 40; i++)
        {
            _temp.CreateFile(16, "cache", $"d{i % 4}", $"f{i}.bin");
        }

        var allowance = new ThreadAllowance(2);
        Assert.True(allowance.TryTake());
        Assert.True(allowance.TryTake());

        var watched = new WatchedDeletions(WindowsFileSystem.Default, TimeSpan.FromMilliseconds(1));
        var fs = new FirstDeletionHooks(watched, new Dictionary<string, Action> { [tree] = allowance.Return });
        var root = new RemovalFolder(LongPath.Extended(tree), parent: null);

        var outcome = TreeRemoval.Run(
            root,
            RemovalBounds.None,
            (listed, stop) =>
            {
                RemovalWalk.Visit(root, MinimumAge.Off, RemovalBounds.None, fs.ListEntries, WalkTuning.Default, listed, stop);
                allowance.Return();
            },
            progress: null,
            planned: 0,
            fs,
            TimeProvider.System,
            gathered: null,
            allowance,
            CancellationToken.None);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(2, watched.MostAtOnce);
    }

    [Fact]
    public async Task ALinkIsNotRemovedWhileEveryPlaceIsTaken()
    {
        var target = _temp.CreateDirectory("target");
        _temp.CreateFile(16, "target", "keep.txt");
        var link = Path.Combine(_temp.Path, "link");
        Junction.ToDirectory(link, target);

        var allowance = Full();
        using var cts = new CancellationTokenSource(Chance);

        var removal = await DirectoryRemover.RemoveAsync(link, ct: cts.Token, deletions: allowance);

        Assert.True(removal.Interrupted);
        Assert.True(Directory.Exists(link), "The link was removed without a place.");

        allowance.Return();
        var after = await DirectoryRemover.RemoveAsync(link, deletions: allowance);

        Assert.True(after.RootRemoved);
        Assert.False(Directory.Exists(link));

        // §5.6's negative: removing the link took nothing it led to.
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public async Task AFileIsNotRemovedWhileEveryPlaceIsTaken()
    {
        var file = _temp.CreateFile(16, "crash.dmp");
        var allowance = Full();
        using var cts = new CancellationTokenSource(Chance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileRemover.RemoveAsync(file, ct: cts.Token, deletions: allowance));
        Assert.True(File.Exists(file), "The file was deleted without a place.");

        allowance.Return();
        var after = await FileRemover.RemoveAsync(file, deletions: allowance);

        Assert.True(after.Removed);
        Assert.True(allowance.TryTake(), "The removal kept a place after it ended.");
    }

    /// <summary>An allowance of one place, already taken, as another removal's deletion takes it.</summary>
    private static ThreadAllowance Full()
    {
        var allowance = new ThreadAllowance(1);
        Assert.True(allowance.TryTake());
        return allowance;
    }
}
