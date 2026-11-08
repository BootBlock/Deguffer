using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A discovery searches its approved folders at the same time where their disks allow it
/// (<see cref="SourceSearchLanes"/>), and its answer is the one a search of one folder after another
/// gives, whatever order the searches finish in.
/// </summary>
public sealed class SourceDirectoryDiscoveryLanesTests : IDisposable
{
    private const byte Nvme = 0x11;

    /// <summary>How long a hold waits before the test fails rather than hangs. Never what a test measures.</summary>
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The first folder's search does not answer until the second's has started, so a discovery that
    /// searched them in turn would never start the second. The second finishes first, and the answer
    /// is still in the order the folders were approved.
    /// </summary>
    [Fact]
    public async Task SearchesFoldersOnASolidStateDiskAtTheSameTime()
    {
        var first = _temp.CreateDirectory("first");
        var second = _temp.CreateDirectory("second");
        var firstObj = Path.Combine(first, "App", "obj");
        var secondObj = Path.Combine(second, "App", "obj");
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scanner = new FakeDirectoryScanner([firstObj, secondObj])
        {
            Answering = async (root, ct) =>
            {
                if (root == second)
                {
                    secondStarted.TrySetResult();
                    return;
                }

                await secondStarted.Task.WaitAsync(Failsafe, ct);
            },
        };

        var found = await Discovery(scanner, SolidState()).FindAsync([new SourceRoot(first), new SourceRoot(second)]).WaitAsync(Failsafe);

        Assert.Equal([firstObj, secondObj], found.Candidates);
    }

    /// <summary>
    /// The same answer as a search of one folder after another, by the walk an unelevated run takes:
    /// a nested folder handed off, a folder approved twice in another case, and a second folder. The
    /// §5.6 negative is part of it: nothing is offered from behind a junction, inside <c>.git</c>, or
    /// inside a candidate.
    /// </summary>
    [Fact]
    public async Task FindsWhatASearchInTurnFinds()
    {
        var parent = _temp.CreateDirectory("parent");
        var work = _temp.CreateDirectory("parent", "work");
        var other = _temp.CreateDirectory("other");
        var outer = _temp.CreateDirectory("parent", "Example", "obj");
        var packages = _temp.CreateDirectory("parent", "web", "node_modules");
        var inner = _temp.CreateDirectory("parent", "work", "App", "obj");
        var lib = _temp.CreateDirectory("other", "Lib", "obj");
        _temp.CreateDirectory("parent", "web", "node_modules", "package", "obj");
        _temp.CreateDirectory("parent", ".git", "obj");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateDirectory("elsewhere", "App", "obj");
        Junction.ToDirectory(Path.Combine(parent, "linked"), elsewhere);

        IReadOnlyList<SourceRoot> roots =
        [
            new SourceRoot(parent), new SourceRoot(other), new SourceRoot(work), new SourceRoot(parent.ToUpperInvariant()),
        ];

        var solidState = SolidState();
        string[] folders = [.. roots.Select(root => root.Path)];

        // The two discoveries take the two routes this test compares: one lane a folder, and one lane.
        Assert.Equal(folders.Length, SourceSearchLanes.Of(folders, solidState.StorageOf).Count);
        Assert.Single(SourceSearchLanes.Of(folders, ScanTuner.Shipped.StorageOf));

        var together = await Discovery(new FakeDirectoryScanner(), solidState).FindAsync(roots);
        var inTurn = await Discovery(new FakeDirectoryScanner(), tuning: null).FindAsync(roots);

        Assert.Equal([outer, packages, lib, inner], together.Candidates);
        Assert.Equal(inTurn.Candidates, together.Candidates);
        Assert.Empty(together.UnreadableDirectories);
        Assert.DoesNotContain(together.Candidates, candidate => candidate.Contains(elsewhere, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One search that fails stops the others, and what is thrown is the failure rather than the stop
    /// it caused. The failed search is not remembered as an answer, so the next caller asks again.
    /// </summary>
    [Fact]
    public async Task OneFailedSearchStopsTheRest()
    {
        var first = _temp.CreateDirectory("first");
        var second = _temp.CreateDirectory("second");
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = true;

        var scanner = new FakeDirectoryScanner([])
        {
            Answering = async (root, ct) =>
            {
                if (!failing)
                {
                    return;
                }

                if (root == second)
                {
                    secondStarted.TrySetResult();

                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        secondStopped.TrySetResult();
                        throw;
                    }

                    return;
                }

                await secondStarted.Task.WaitAsync(Failsafe, ct);
                throw new IOException("The volume index could not be read.");
            },
        };

        var discovery = Discovery(scanner, SolidState());
        IReadOnlyList<SourceRoot> roots = [new SourceRoot(first), new SourceRoot(second)];

        var thrown = await Assert.ThrowsAsync<IOException>(() => discovery.FindAsync(roots).WaitAsync(Failsafe));

        Assert.Equal("The volume index could not be read.", thrown.Message);
        Assert.True(secondStopped.Task.IsCompleted);

        failing = false;
        var found = await discovery.FindAsync(roots).WaitAsync(Failsafe);

        Assert.Equal(4, scanner.FindCalls);
        Assert.Empty(found.Candidates);
    }

    /// <summary>The scratch tree's volume, on one NVMe disk, so its folders are searched together.</summary>
    private ScanTuner SolidState()
    {
        var root = Path.GetPathRoot(_temp.Path)!;

        return new ScanTuner(
            new FakePreferences(AppPreferences.Default),
            new VolumeMediaCache(new FakeStorageQueries().Volume(root, 1).Disk(1, Nvme, seekPenalty: false)),
            new FakeVolumeInventory().With(root));
    }

    private SourceDirectoryDiscovery Discovery(FakeDirectoryScanner scanner, ScanTuner? tuning)
    {
        var discovery = new SourceDirectoryDiscovery(
            scanner, new FakeVolumeInventory().With(Path.GetPathRoot(_temp.Path)!), tuning);

        discovery.Include(["obj", "node_modules"]);

        return discovery;
    }
}
