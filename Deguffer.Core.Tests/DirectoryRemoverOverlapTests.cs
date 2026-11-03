using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A removal deletes inside each folder as soon as that folder's own listing is complete, while the
/// rest of the tree is still being listed, and acts on nothing a complete listing did not produce.
/// </summary>
public sealed class DirectoryRemoverOverlapTests : IDisposable
{
    /// <summary>
    /// How long a listing held for a deletion waits for it. A removal that lists the whole tree before
    /// it deletes anything never deletes while a listing is held, so this is how long the test takes to
    /// fail against one.
    /// </summary>
    private static readonly TimeSpan DeletionWait = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A file is deleted before a folder listed after the one holding it has been listed. The folder
    /// inside it is the one held, because it cannot be listed before its parent's listing is complete,
    /// whatever order the walk's workers take the tree in.
    /// </summary>
    [Fact]
    public async Task DeletesAFolderFilesBeforeTheRestOfTheTreeIsListed()
    {
        var root = _temp.CreateDirectory("cache");
        var early = _temp.CreateFile(100, "cache", "a", "early.bin");
        var deeper = _temp.CreateDirectory("cache", "a", "deeper");
        _temp.CreateFile(50, "cache", "a", "deeper", "late.bin");
        var neighbour = _temp.CreateFile(64, "neighbour", "keep.bin");
        var goneFirst = false;

        var fs = new ListingHookFileSystem(
            WindowsFileSystem.Default,
            deeper,
            () => goneFirst = SpinWait.SpinUntil(() => !File.Exists(early), DeletionWait));

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, default, fs);

        Assert.True(goneFirst, "nothing was deleted until the whole tree had been listed");
        Assert.True(outcome.RootRemoved);
        Assert.False(outcome.Interrupted);
        Assert.Equal(150, outcome.BytesReclaimed);

        // cache, a, deeper and their two files.
        Assert.Equal(5, outcome.EntriesRemoved);
        Assert.Empty(outcome.LeftStanding);

        // §5.6: the folder beside the root was never the removal's to take.
        Assert.True(File.Exists(neighbour), "the removal reached outside its root");
    }

    /// <summary>
    /// A removal cancelled while it is still listing returns what it took and says it was stopped. It
    /// names exactly the folders it went into and left: the root, the folder whose file went, and the
    /// folder it was listing, whose own file is still there.
    /// </summary>
    [Fact]
    public async Task ARemovalCancelledWhileItListsReportsExactlyWhatWent()
    {
        var root = _temp.CreateDirectory("cache");
        var early = _temp.CreateFile(100, "cache", "a", "early.bin");
        var deeper = _temp.CreateDirectory("cache", "a", "deeper");
        var late = _temp.CreateFile(50, "cache", "a", "deeper", "late.bin");
        var neighbour = _temp.CreateFile(64, "neighbour", "keep.bin");
        using var cts = new CancellationTokenSource();
        var goneFirst = false;

        var fs = new ListingHookFileSystem(
            WindowsFileSystem.Default,
            deeper,
            () =>
            {
                goneFirst = SpinWait.SpinUntil(() => !File.Exists(early), DeletionWait);
                cts.Cancel();
            });

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, cts.Token, fs);

        Assert.True(goneFirst, "nothing was deleted until the whole tree had been listed");
        Assert.True(outcome.Interrupted);
        Assert.False(outcome.RootRemoved);
        Assert.Equal(100, outcome.BytesReclaimed);
        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.Equal(
            new[] { root, Path.Combine(root, "a"), deeper }.Order(StringComparer.OrdinalIgnoreCase),
            outcome.LeftStanding.Order(StringComparer.OrdinalIgnoreCase));

        // §5.6: what the removal had not reached is still there, and so is everything outside it.
        Assert.True(File.Exists(late), "a file in a folder the removal stopped listing was deleted");
        Assert.True(File.Exists(neighbour), "the removal reached outside its root");
    }

    /// <summary>
    /// A folder whose listing Windows refuses part-way loses nothing, although the listing handed over
    /// every entry before the refusal, while the folders beside it are removed as usual.
    /// </summary>
    [Fact]
    public async Task LosesNothingFromAFolderWhoseListingIsRefusedPartWay()
    {
        var root = _temp.CreateDirectory("cache");
        var refused = _temp.CreateDirectory("cache", "refused");
        var kept = new[]
        {
            _temp.CreateFile(10, "cache", "refused", "x.bin"),
            _temp.CreateFile(10, "cache", "refused", "y.bin"),
        };
        var removed = _temp.CreateFile(20, "cache", "other", "z.bin");
        var neighbour = _temp.CreateFile(64, "neighbour", "keep.bin");

        var outcome = await DirectoryRemover.RemoveAsync(
            root,
            MinimumAge.Off,
            progress: null,
            default,
            new UnlistableFileSystem(WindowsFileSystem.Default, refused, partWay: true));

        Assert.All(kept, file => Assert.True(File.Exists(file), $"{file} was deleted from a folder never read to the end"));
        Assert.False(File.Exists(removed));
        Assert.False(Directory.Exists(Path.Combine(root, "other")));
        Assert.Equal(20, outcome.BytesReclaimed);
        Assert.False(outcome.Interrupted);
        Assert.False(outcome.RootRemoved);
        Assert.Contains(refused, outcome.LeftStanding);
        Assert.Contains(root, outcome.LeftStanding);
        Assert.True(File.Exists(neighbour), "the removal reached outside its root");
    }
}
