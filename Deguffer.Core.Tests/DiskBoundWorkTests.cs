using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Work on each of a list of paths that waits on the disk, such as the recognition of every candidate
/// a source-tree row finds: several at once, never more than the volume is tuned for, and answered in
/// the list's order whatever order the work finishes in.
///
/// <para>The paths are invented and never touched: the work under test is the test's own. They are on
/// a share of their own, so no walk another test runs shares their volume's slots. Each wait is
/// bounded, so a run that works one path at a time fails on its assertion rather than hanging.</para>
/// </summary>
public sealed class DiskBoundWorkTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A path whose work waits until a second path is being worked still completes. Worked one at a
    /// time, the first would wait for a second that never starts.
    /// </summary>
    [Fact]
    public async Task APathWaitingForAnotherToStartIsNotLeftWaiting()
    {
        using var both = new Barrier(2);

        var met = await DiskBoundWork.EachAsync(Paths(2), ScanTuner.Shipped, _ => both.SignalAndWait(Patience), default);

        Assert.Equal([true, true], met);
    }

    /// <summary>
    /// Every answer is in its path's place in the list, though each path's work finishes only after the
    /// one listed after it has. A caller sorting answers into targets and declined keeps its search's
    /// order, so every count and note reads as it did.
    /// </summary>
    [Fact]
    public async Task AnswersKeepTheListsOrderWhenTheWorkFinishesInReverse()
    {
        const int Count = 6;

        var paths = Paths(Count);
        var finished = Enumerable.Range(0, Count).Select(_ => new ManualResetEventSlim()).ToArray();
        var order = new List<int>();

        try
        {
            // Tuned to the whole chain, which finishes only if every link runs at once. The shipped
            // number follows the processor count, and falls below six on a machine of two.
            var answers = await DiskBoundWork.EachAsync(
                paths,
                TunedTo(Count),
                path =>
                {
                    var index = paths.IndexOf(path);
                    var waited = index == Count - 1 || finished[index + 1].Wait(Patience);

                    lock (order)
                    {
                        order.Add(index);
                    }

                    finished[index].Set();
                    return (path, waited);
                },
                default);

            Assert.Equal(Enumerable.Range(0, Count).Reverse(), order);
            Assert.Equal(paths, answers.Select(answer => answer.path));
            Assert.All(answers, answer => Assert.True(answer.waited));
        }
        finally
        {
            foreach (var done in finished)
            {
                done.Dispose();
            }
        }
    }

    /// <summary>
    /// No more paths are worked at once than a walk of the volume is tuned to list, however many are
    /// asked for. Each waits until the bound's worth are under way, and then a while longer, in which
    /// one past the bound would have started too.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task NoMorePathsAreWorkedAtOnceThanTheVolumeIsTunedFor(int threads)
    {
        var running = 0;
        var peak = 0;

        await DiskBoundWork.EachAsync(
            Paths(4 * threads + 1),
            TunedTo(threads),
            _ =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);

                SpinWait.SpinUntil(() => Volatile.Read(ref running) >= threads, Patience);
                Thread.Sleep(20);

                Interlocked.Decrement(ref running);
                return now;
            },
            default);

        Assert.Equal(threads, peak);
    }

    /// <summary>
    /// The work runs on a thread of the walk's own, never the pool's: each item blocks its thread on
    /// the disk, and the pool grows past its minimum only once it finds itself starved.
    /// </summary>
    [Fact]
    public async Task TheWorkIsDoneOffThePool()
    {
        var onThePool = new List<bool>();

        var made = await CrewThreads.MadeBy(() => DiskBoundWork.EachAsync(
            Paths(3),
            ScanTuner.Shipped,
            _ =>
            {
                lock (onThePool)
                {
                    onThePool.Add(Thread.CurrentThread.IsThreadPoolThread);
                }

                return 0;
            },
            default));

        CrewThreads.AssertOffThePool(made);
        Assert.Equal([false, false, false], onThePool);
    }

    /// <summary>A failure in one path's work reaches the caller unchanged, not wrapped.</summary>
    [Fact]
    public async Task AFailureReachesTheCallerUnchanged()
    {
        var failure = new IOException("refused");

        var thrown = await Assert.ThrowsAsync<IOException>(() => DiskBoundWork.EachAsync<int>(
            Paths(4),
            ScanTuner.Shipped,
            path => path.Contains("Project2", StringComparison.Ordinal) ? throw failure : 0,
            default));

        Assert.Same(failure, thrown);
    }

    /// <summary>A cancelled request works no path at all.</summary>
    [Fact]
    public async Task ACancelledRequestWorksNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var worked = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskBoundWork.EachAsync(
            Paths(4),
            ScanTuner.Shipped,
            _ => Interlocked.Increment(ref worked),
            cancelled.Token));

        Assert.Equal(0, worked);
    }

    private static List<string> Paths(int count) =>
        [.. Enumerable.Range(0, count).Select(index => $@"\\volume.test\source\Project{index}\obj")];

    /// <summary>A walk of <paramref name="threads"/> on every volume: the tuner knows no drive's kind, so each is unknown.</summary>
    private static ScanTuner TunedTo(int threads) => new(new FakePreferences(AppPreferences.Default with
    {
        Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: threads)),
    }));

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);

        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref target, value, seen);

            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }
}
