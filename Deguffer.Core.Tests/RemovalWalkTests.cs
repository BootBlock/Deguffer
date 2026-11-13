using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk a removal makes of its tree, gathered whole, on the walk every scan takes: several workers,
/// no recursion, a listing that reports a refusal rather than throwing it, and the attributes the
/// listing read kept for the deletion that follows.
/// </summary>
public sealed class RemovalWalkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// What the walk gathers is the same however many workers gathered it, in the same order. A removal
    /// reads its folders in reverse to record the ones left standing, so an order that followed how the
    /// workers interleaved would report the same removal differently from one run to the next.
    /// </summary>
    [Fact]
    public void GathersTheSameTreeWithOneWorkerAsWithMany()
    {
        var root = BuildTree();
        var refused = _temp.CreateDirectory("cache", "branch-1", "refused");
        _temp.CreateFile(64, "cache", "branch-1", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        var keep = MinimumAge.Within(TimeSpan.FromDays(1), DateTime.UtcNow);
        var bounds = new RemovalBounds(KeepRoot: false, [Path.Combine(root, "branch-0", "spared")]);

        var one = Describe(Gather(root, keep, bounds, new WalkTuning(1, WalkTuning.MinimumListingBuffer)));

        Assert.Contains(Path.Combine(LongPath.Extended(root), "wide", "file-0499.bin"), one.Files);
        Assert.Contains(LongPath.Extended(refused), one.Directories);
        Assert.Single(one.Spared);
        Assert.Single(one.MailStores);
        Assert.Single(one.Links);
        Assert.Equal(1, one.Kept);

        foreach (var tuning in new[]
        {
            new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer),
            new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MaximumListingBuffer),
            WalkTuning.Default,
        })
        {
            var many = Describe(Gather(root, keep, bounds, tuning));

            Assert.Equal(one.Directories, many.Directories);
            Assert.Equal(one.Files, many.Files);
            Assert.Equal(one.Links, many.Links);
            Assert.Equal(one.Spared, many.Spared);
            Assert.Equal(one.MailStores, many.MailStores);
            Assert.Equal(one.Kept, many.Kept);
        }
    }

    /// <summary>
    /// A folder and every file gathered from it share one path, the listing's own. A removal holds a
    /// folder's files until the last of them goes, so a second copy of its path would be held that long.
    /// The bounds hold back an entry in one folder, so the walk asks its bounds as it does where one is.
    /// </summary>
    [Fact]
    public void AFolderAndTheFilesGatheredFromItShareOnePath()
    {
        var root = BuildTree();
        var bounds = new RemovalBounds(KeepRoot: false, [Path.Combine(root, "branch-0", "spared")]);

        var folders = Gather(root, MinimumAge.Off, bounds, new WalkTuning(1, WalkTuning.MinimumListingBuffer)).InWalkOrder();

        Assert.NotEmpty(folders.SelectMany(folder => folder.Files));
        Assert.All(folders, folder => Assert.All(folder.Files, file => Assert.Same(folder.Path, file.Directory)));
    }

    /// <summary>
    /// Every directory comes after the one it was found in, which is what lets a removal record children
    /// before their parent by reading the list backwards.
    /// </summary>
    [Fact]
    public void ListsEachDirectoryAfterTheOneItWasFoundIn()
    {
        var root = BuildTree();

        var paths = Gather(
                root, MinimumAge.Off, RemovalBounds.None, new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer))
            .InWalkOrder()
            .Select(folder => folder.Path)
            .ToList();

        Assert.Equal(LongPath.Extended(root), paths[0]);

        for (var index = 1; index < paths.Count; index++)
        {
            Assert.InRange(paths.IndexOf(Path.GetDirectoryName(paths[index])!), 0, index - 1);
        }
    }

    /// <summary>
    /// A folder Windows will not list is skipped as §5.3 says, and the walk learns that from the
    /// listing's answer rather than from an exception: a removal of a large tree meets many such
    /// folders, and each thrown one is a stack unwind that costs more than the listing it ends.
    /// </summary>
    [Fact]
    public void SkipsAFolderItMayNotListWithoutThrowing()
    {
        var root = _temp.CreateDirectory("cache");
        var kept = _temp.CreateFile(32, "cache", "readable", "kept.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(64, "cache", "refused", "unreachable.bin");

        using var denied = new DeniedDirectory(refused);

        // One worker, which is the calling thread: an exception on a helper would not be seen here,
        // because the walk starts its helpers without the context that records what is thrown.
        Described? gathered = null;
        var thrown = ThrownExceptions.During(
            () => gathered = Describe(Gather(root, MinimumAge.Off, RemovalBounds.None, new WalkTuning(1, WalkTuning.MinimumListingBuffer))));

        Assert.Empty(thrown);
        Assert.Contains(LongPath.Extended(refused), gathered!.Directories);
        Assert.Equal([LongPath.Extended(kept)], gathered.Files);
    }

    /// <summary>
    /// The read-only bit the listing saw is cleared before the file's first delete. Waiting for Windows
    /// to refuse the delete cost a thrown exception and a second delete for every such file, and package
    /// manager caches set the bit on all of them.
    /// </summary>
    [Fact]
    public async Task ClearsAReadOnlyBitBeforeTheFirstDelete()
    {
        var root = _temp.CreateDirectory("cache");
        var locked = _temp.CreateFile(128, "cache", "pkg", "locked.bin");
        var plain = _temp.CreateFile(64, "cache", "pkg", "plain.bin");
        var beside = _temp.CreateFile(16, "beside.bin");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        RemovalOutcome? outcome = null;

        var thrown = ThrownExceptions.During(
            () => outcome = DirectoryRemover.RemoveAsync(root, fileSystem: recording).GetAwaiter().GetResult());

        Assert.DoesNotContain(thrown, ex => ex is UnauthorizedAccessException);
        Assert.True(outcome!.RootRemoved);
        Assert.Equal(192, outcome.BytesReclaimed);

        // Cleared once and deleted once. A delete refused for the bit would add a second delete.
        Assert.Equal(2, recording.Paths.Count(path => path == LongPath.Extended(locked)));
        Assert.Equal(1, recording.Paths.Count(path => path == LongPath.Extended(plain)));

        // §6.3 through the seam, and §5.6: what stood beside the tree is still there.
        Assert.All(recording.Paths, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
        Assert.True(File.Exists(beside));
    }

    /// <summary>
    /// A file last written past the year 9999 is newer than any window, so the guard keeps it and the
    /// removal takes the rest around it. The listing once made a <see cref="DateTime"/> of the time,
    /// which threw out of the removal's walk. §5.6: what the guard keeps, and what stood beside the
    /// tree, are still on the disk.
    /// </summary>
    [Fact]
    public async Task KeepsAFileWrittenPastTheYear9999AndRemovesTheRest()
    {
        var root = _temp.CreateDirectory("cache");
        var longAgo = DateTime.UtcNow - TimeSpan.FromDays(30);
        var stale = _temp.CreateFile(64, "cache", "pkg", "stale.bin");
        var dated = _temp.CreateFile(128, "cache", "pkg", "dated.bin");
        var beside = _temp.CreateFile(16, "beside.bin");
        EntryTime.Set(stale, longAgo);
        EntryTime.SetFileTimes(dated, longAgo.ToFileTimeUtc(), EntryTime.PastTheLastDateTime);

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(dated));
        Assert.True(File.Exists(beside));
        Assert.False(outcome.RootRemoved);
        Assert.Equal(1, outcome.Kept);
        Assert.Equal(64, outcome.BytesReclaimed);
    }

    /// <summary>
    /// A tree deeper than a thread's stack could hold one frame per level of. The walk this replaced
    /// recursed once per folder, and a stack overflow is not an exception anything can catch: it ends
    /// the process.
    /// </summary>
    [Fact]
    public void GathersATreeDeeperThanTheStackCouldRecurseThrough()
    {
        const int Depth = 3000;

        var root = _temp.CreateDirectory("deep");
        var deepest = Path.Combine([root, .. Enumerable.Repeat("d", Depth)]);
        Directory.CreateDirectory(LongPath.Extended(deepest));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deepest, "leaf.bin")), new byte[8]);

        RemovalWalk.Folder? gathered = null;

        // A small stack, so the claim does not rest on how large a frame this build happens to make.
        var walker = new Thread(
            () => gathered = RemovalWalk.Gather(
                LongPath.Extended(root), MinimumAge.Off, RemovalBounds.None, WindowsFileSystem.Default.ListEntries, default),
            maxStackSize: 256 * 1024);

        walker.Start();
        walker.Join();

        var described = Describe(gathered!);

        Assert.Equal(Depth + 1, described.Directories.Count);
        Assert.Equal(LongPath.Extended(Path.Combine(deepest, "leaf.bin")), Assert.Single(described.Files));
    }

    /// <summary>
    /// What gathering allocates for each file. The listing once built a <see cref="FileSystemInfo"/>
    /// and a full path for every entry, on top of the entry it was turned into; the walk keeps the
    /// file's name and builds the path only where the deletion asks for it.
    /// </summary>
    [Fact]
    public void AllocatesLittleMoreThanItsNameForEachFile()
    {
        const int Files = 2000;

        var root = _temp.CreateDirectory("cache", "a-folder-with-a-name-as-long-as-a-package-cache-gives-one");
        for (var index = 0; index < Files; index++)
        {
            _temp.CreateFile(0, "cache", "a-folder-with-a-name-as-long-as-a-package-cache-gives-one", $"f{index:D4}.bin");
        }

        // One worker, which is the calling thread, so what this thread allocated is what the walk did.
        var oneWorker = new WalkTuning(1, WalkTuning.MinimumListingBuffer);
        Gather(root, MinimumAge.Off, RemovalBounds.None, oneWorker);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var gathered = Gather(root, MinimumAge.Off, RemovalBounds.None, oneWorker);
        var perFile = (GC.GetAllocatedBytesForCurrentThread() - before) / Files;

        Assert.Equal(Files, gathered.InWalkOrder().Sum(folder => folder.Files.Count));

        // The entry in the walk's listing and in the folder, and its name: about 430 bytes in a
        // Debug build, some of it the runtime's own. Listing through a FileSystemInfo per entry, as the
        // removal once did, measured about 880 here before the walk kept a full path as well.
        Assert.InRange(perFile, 1, 600);
    }

    private string BuildTree()
    {
        var root = _temp.CreateDirectory("cache");

        for (var index = 0; index < 500; index++)
        {
            _temp.CreateFile(index % 7, "cache", "wide", $"file-{index:D4}.bin");
        }

        for (var branch = 0; branch < 6; branch++)
        {
            var path = new List<string> { "cache", $"branch-{branch}" };
            for (var level = 0; level < 5; level++)
            {
                path.Add($"level-{level}");
                _temp.CreateFile(level + 1, [.. path, $"at-{level}.bin"]);
            }
        }

        var old = DateTime.UtcNow.AddDays(-2);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, old);
            File.SetLastWriteTimeUtc(file, old);
        }

        // Written after the ageing, so the guard keeps it.
        _temp.CreateFile(8, "cache", "branch-2", "recent.bin");

        var readOnly = _temp.CreateFile(16, "cache", "branch-3", "read-only.bin");
        File.SetCreationTimeUtc(readOnly, old);
        File.SetLastWriteTimeUtc(readOnly, old);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        var store = _temp.CreateFile(32, "cache", "branch-4", "archive.pst");
        File.SetCreationTimeUtc(store, old);
        File.SetLastWriteTimeUtc(store, old);

        var spared = _temp.CreateDirectory("cache", "branch-0", "spared");
        _temp.CreateFile(8, "cache", "branch-0", "spared", "inside.bin");

        var target = _temp.CreateDirectory("elsewhere");
        Junction.ToDirectory(Path.Combine(root, "branch-5", "shortcut"), target);
        Directory.SetCreationTimeUtc(Path.Combine(root, "branch-5", "shortcut"), old);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "branch-5", "shortcut"), old);

        return root;
    }

    private static RemovalWalk.Folder Gather(string root, MinimumAge keep, RemovalBounds bounds, WalkTuning tuning) =>
        RemovalWalk.Gather(LongPath.Extended(root), keep, bounds, WindowsFileSystem.Default.ListEntries, tuning, default);

    /// <summary>Everything gathered at and below <paramref name="root"/>, folder by folder in walk order.</summary>
    private static Described Describe(RemovalWalk.Folder root)
    {
        var folders = root.InWalkOrder();

        return new(
            [.. folders.Select(folder => folder.Path)],
            [.. folders.SelectMany(folder => folder.Files).Select(file => file.FullName)],
            [.. folders.SelectMany(folder => folder.Links).Select(link => link.FullName)],
            [.. folders.SelectMany(folder => folder.Spared)],
            [.. folders.SelectMany(folder => folder.MailStores)],
            folders.Sum(folder => folder.Kept));
    }

    private sealed record Described(
        IReadOnlyList<string> Directories,
        IReadOnlyList<string> Files,
        IReadOnlyList<string> Links,
        IReadOnlyList<string> Spared,
        IReadOnlyList<string> MailStores,
        int Kept);
}
