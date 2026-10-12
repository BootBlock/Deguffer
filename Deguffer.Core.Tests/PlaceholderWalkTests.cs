using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk of one sync root as its folders are read in parallel: the same selection whatever the
/// number of workers, and more than one placeholder described at once.
/// </summary>
public sealed class PlaceholderWalkTests
{
    private const string Root = @"C:\Users\testuser\OneDrive";

    private const string OneDriveId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Personal";

    private static string At(params string[] segments) => Path.Combine([Root, .. segments]);

    /// <summary>
    /// A root with something of every kind in many folders: files that go, files each rule holds back,
    /// a pinned folder, a folder Windows will not describe, an ordinary folder, and a link with files
    /// behind it.
    /// </summary>
    private static FakeCloudFiles SyncRoot()
    {
        var cloud = new FakeCloudFiles().Root(OneDriveId, Root, "OneDrive - Personal");

        for (var folder = 0; folder < 24; folder++)
        {
            var name = $"Folder {folder:D2}";

            if (folder % 6 == 5)
            {
                cloud.PlainFolder(At(name));
            }
            else
            {
                cloud.Folder(At(name), folder == 4 ? PinState.Pinned : PinState.Unspecified);
            }

            for (var file = 0; file < 20; file++)
            {
                var path = At(name, $"file {file:D2}.bin");

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
            .Folder(At("Locked"))
            .Refuse(At("Locked"))
            .File(At("Locked", "inside.bin"), onDisk: 100)
            .Link(At("Elsewhere"))
            .File(At("Elsewhere", "beyond.bin"), onDisk: 100)
            .PlainFile(At("ordinary.txt"));

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

        var one = PlaceholderWalk.Of(cloud, Root, MinimumAge.Off, threads: 1, CancellationToken.None);

        for (var run = 0; run < 5; run++)
        {
            var many = PlaceholderWalk.Of(cloud, Root, MinimumAge.Off, threads: 8, CancellationToken.None);

            Assert.Equal(one.Files, many.Files);
            Assert.Equal(one.Held.OrderBy(h => h.Key), many.Held.OrderBy(h => h.Key));
            Assert.Equal(one.Unreadable, many.Unreadable);
        }

        // Fourteen files in each of twenty-three folders: the pinned folder's twenty, and the six each
        // folder holds back for its own pin or for edits the cloud lacks, stay.
        Assert.Equal(23 * 14, one.Files.Count);
        Assert.True(one.HeldFor(HeldBack.Pinned).Files >= 23 * 3);
        Assert.True(one.HeldFor(HeldBack.NotInSync).Files > 0);
        Assert.Equal(1, one.Unreadable);
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Elsewhere"), f.Path));
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Locked"), f.Path));
        Assert.DoesNotContain(one.Files, f => LongPath.Contains(At("Folder 04"), f.Path));
    }

    /// <summary>
    /// The point of the change: a root of many folders has more than one placeholder described at
    /// once. The first reads below the root wait for a second to begin, so a walk that read one at a
    /// time would see one in flight and no more. The root's own folders are read by one worker before
    /// any is queued, so a wait there would prove nothing and only use up the patience.
    /// </summary>
    [Fact]
    public void DescribesPlaceholdersInSeveralFoldersAtOnce()
    {
        var cloud = new OverlapWatch(SyncRoot(), holdBelow: Root);

        PlaceholderWalk.Of(cloud, Root, MinimumAge.Off, threads: 4, CancellationToken.None);

        Assert.True(cloud.MostAtOnce >= 2, $"At most {cloud.MostAtOnce} placeholder was described at once.");
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => PlaceholderWalk.Of(SyncRoot(), Root, MinimumAge.Off, threads: 4, cancelled.Token));
    }

    /// <summary>
    /// Counts the descriptions in flight, and holds each of the first few below
    /// <paramref name="holdBelow"/>'s own entries until a second has begun or a timeout has passed, so
    /// overlap that the walk allows is observed rather than left to chance.
    /// </summary>
    private sealed class OverlapWatch(ICloudFiles inner, string holdBelow) : ICloudFiles
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private int _inFlight;
        private int _mostAtOnce;
        private int _held;

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public PlaceholderReading Read(string path)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(now);

            try
            {
                var inSubfolder = Path.GetDirectoryName(path) is { } folder
                    && LongPath.Contains(holdBelow, folder)
                    && !string.Equals(folder, holdBelow, StringComparison.OrdinalIgnoreCase);

                if (inSubfolder && Interlocked.Increment(ref _held) <= 4)
                {
                    SpinWait.SpinUntil(() => MostAtOnce >= 2, Patience);
                }

                return inner.Read(path);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public IReadOnlyList<SyncRoot>? SyncRoots() => inner.SyncRoots();

        public string? Resolve(string path) => inner.Resolve(path);

        public SyncProviderState ProviderState(string syncRoot) => inner.ProviderState(syncRoot);

        public IEnumerable<CloudEntry> List(string directory, CancellationToken ct) => inner.List(directory, ct);

        public ReleaseAnswer Release(string path, string resolvedPath, Func<Placeholder, bool> stillEligible) =>
            inner.Release(path, resolvedPath, stillEligible);

        private void InterlockedMax(int value)
        {
            var seen = Volatile.Read(ref _mostAtOnce);
            while (value > seen)
            {
                var previous = Interlocked.CompareExchange(ref _mostAtOnce, value, seen);
                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
