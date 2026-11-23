using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk of sync roots as their folders are read in parallel: the same selection whatever the
/// number of workers, more than one placeholder described at once, and several roots sharing one
/// walk's threads with each tallied apart.
/// </summary>
public sealed class PlaceholderWalkTests
{
    private const string Root = @"C:\Users\testuser\OneDrive";

    private const string OneDriveId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Personal";

    private const string Work = @"C:\Users\testuser\OneDrive - Contoso";

    private const string WorkId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Business1";

    /// <summary>How long a description that must have company waits for it before the test fails.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a description waits for company that would prove the walk read more at once than it
    /// may. Paid in full by each of the first few descriptions when the walk is right.
    /// </summary>
    private static readonly TimeSpan Company = TimeSpan.FromSeconds(1);

    private static string At(params string[] segments) => In(Root, segments);

    private static string In(string root, params string[] segments) => Path.Combine([root, .. segments]);

    /// <summary>
    /// A root with something of every kind in many folders: files that go, files each rule holds back,
    /// a pinned folder, a folder Windows will not describe, an ordinary folder, and a link with files
    /// behind it.
    /// </summary>
    private static FakeCloudFiles SyncRoot() => SyncRoot(new FakeCloudFiles(), Root, OneDriveId);

    /// <summary><paramref name="root"/>, holding what <see cref="SyncRoot()"/>'s does, added to <paramref name="cloud"/>.</summary>
    private static FakeCloudFiles SyncRoot(FakeCloudFiles cloud, string root, string id)
    {
        cloud.Root(id, root, Path.GetFileName(root));

        for (var folder = 0; folder < 24; folder++)
        {
            var name = $"Folder {folder:D2}";

            if (folder % 6 == 5)
            {
                cloud.PlainFolder(In(root, name));
            }
            else
            {
                cloud.Folder(In(root, name), folder == 4 ? PinState.Pinned : PinState.Unspecified);
            }

            for (var file = 0; file < 20; file++)
            {
                var path = In(root, name, $"file {file:D2}.bin");

                switch (file % 7)
                {
                    case 3:
                        cloud.File(path, onDisk: 300, pin: PinState.Pinned);
                        break;

                    case 5:
                        cloud.File(path, onDisk: 500, inSync: false);
                        break;

                    default:
                        cloud.File(path, onDisk: 1_000 + file);
                        break;
                }
            }
        }

        cloud
            .Folder(In(root, "Locked"))
            .Refuse(In(root, "Locked"))
            .File(In(root, "Locked", "inside.bin"), onDisk: 100)
            .Link(In(root, "Elsewhere"))
            .File(In(root, "Elsewhere", "beyond.bin"), onDisk: 100)
            .PlainFile(In(root, "ordinary.txt"));

        return cloud;
    }

    /// <summary>
    /// Many workers finish their folders in whatever order the threads run, and the selection they
    /// return must be the one a single worker returns, file for file and in the same order. The link's
    /// file and the refused folder's file are in neither (§5.2), so parallel reading reaches nothing
    /// the single walk would not.
    /// </summary>
    [Fact]
    public void AnyNumberOfWorkersFindsTheSameSelection()
    {
        var cloud = SyncRoot();

        var one = Assert.Single(PlaceholderWalk.Of(cloud, [Root], MinimumAge.Off, threads: 1, CancellationToken.None));

        for (var run = 0; run < 5; run++)
        {
            var many = Assert.Single(PlaceholderWalk.Of(cloud, [Root], MinimumAge.Off, threads: 8, CancellationToken.None));

            Assert.Equal(one.Files, many.Files);
            Assert.Equal(one.Held.OrderBy(h => h.Key), many.Held.OrderBy(h => h.Key));
            Assert.Equal(one.Unreadable, many.Unreadable);
        }

        // Fourteen files in each of twenty-three folders: the pinned folder's twenty, and the six each
        // folder holds back for its own pin or for edits the cloud lacks, stay. Sync is judged before
        // the pin, so the pinned folder's three unsynced files count as unsynced.
        Assert.Equal(23 * 14, one.Files.Count);
        Assert.Equal((23 * 3) + 17, one.HeldFor(HeldBack.Pinned).Files);
        Assert.Equal(24 * 3, one.HeldFor(HeldBack.NotInSync).Files);
        Assert.Equal(1, one.Unreadable);
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Elsewhere"), f.Path));
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Locked"), f.Path));
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Folder 04"), f.Path));
    }

    /// <summary>
    /// The point of the change: a root of many folders has more than one placeholder described at
    /// once. The first reads below the root wait for a second to begin, so a walk that read one at a
    /// time would see one in flight and no more.
    /// </summary>
    [Fact]
    public void DescribesPlaceholdersInSeveralFoldersAtOnce()
    {
        var cloud = new PlaceholderOverlapWatch(SyncRoot(), InASubfolderOf(Root), wanted: 2, Patience);

        PlaceholderWalk.Of(cloud, [Root], MinimumAge.Off, threads: 4, CancellationToken.None);

        Assert.True(cloud.MostAtOnce >= 2, $"At most {cloud.MostAtOnce} placeholder was described at once.");
    }

    /// <summary>
    /// Roots walked in one run are tallied apart: each gets exactly the selection its own walk finds,
    /// in the order the roots were given, and nothing found under one is counted under another.
    /// </summary>
    [Fact]
    public void EachRootInOneRunFindsWhatItsOwnWalkFinds()
    {
        var cloud = SyncRoot(SyncRoot(), Work, WorkId);
        var alone = new[] { Root, Work }
            .Select(root => Assert.Single(PlaceholderWalk.Of(cloud, [root], MinimumAge.Off, threads: 1, CancellationToken.None)))
            .ToArray();

        for (var run = 0; run < 5; run++)
        {
            var together = PlaceholderWalk.Of(cloud, [Work, Root], MinimumAge.Off, threads: 8, CancellationToken.None);

            Assert.Equal(2, together.Count);
            Assert.Equal(alone[1].Files, together[0].Files);
            Assert.Equal(alone[0].Files, together[1].Files);
            Assert.Equal(alone[1].Held.OrderBy(h => h.Key), together[0].Held.OrderBy(h => h.Key));
            Assert.Equal(alone[0].Held.OrderBy(h => h.Key), together[1].Held.OrderBy(h => h.Key));
            Assert.Equal(alone[1].Unreadable, together[0].Unreadable);
            Assert.Equal(alone[0].Unreadable, together[1].Unreadable);
        }

        Assert.All(alone[0].Files, f => Assert.True(LongPath.Contains(Root, f.Path)));
        Assert.All(alone[1].Files, f => Assert.True(LongPath.Contains(Work, f.Path)));
        Assert.Equal(23 * 14, alone[1].Files.Count);
    }

    /// <summary>
    /// Three roots of loose files in one run of two threads: two are read at once, because the run
    /// shares its threads between them, and never three, because it is one walk and not one for each
    /// root. Walked one after another, as before, each root is read by one thread alone, and one
    /// placeholder is described at a time.
    /// </summary>
    [Fact]
    public void RootsInOneRunShareItsThreads()
    {
        var cloud = new FakeCloudFiles();
        string[] roots = [@"C:\Sync\One", @"C:\Sync\Two", @"C:\Sync\Three"];

        foreach (var root in roots)
        {
            cloud.Root($"OneDrive!{Path.GetFileName(root)}", root, Path.GetFileName(root));

            for (var file = 0; file < 4; file++)
            {
                cloud.File(In(root, $"file {file}.bin"), onDisk: 100);
            }
        }

        // Every description waits for a third to begin, so a third the walk let start is seen.
        var watch = new PlaceholderOverlapWatch(cloud, static _ => true, wanted: 3, Company);

        var found = PlaceholderWalk.Of(watch, roots, MinimumAge.Off, threads: 2, CancellationToken.None);

        Assert.Equal(2, watch.MostAtOnce);
        Assert.All(found, selection => Assert.Equal(4, selection.Files.Count));
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => PlaceholderWalk.Of(SyncRoot(), [Root], MinimumAge.Off, threads: 4, cancelled.Token));
    }

    /// <summary>
    /// Whether a description is of an entry below <paramref name="root"/>'s own. The root's own entries
    /// are read by one worker before any folder is stacked, so a wait there would prove nothing and only
    /// use up the patience.
    /// </summary>
    private static Func<string, bool> InASubfolderOf(string root) =>
        path => Path.GetDirectoryName(path) is { } folder
            && LongPath.Contains(root, folder)
            && !string.Equals(folder, root, StringComparison.OrdinalIgnoreCase);
}
