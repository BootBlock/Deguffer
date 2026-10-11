using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// When a walk in progress starts a snapshot, and how many it can start however long each one
/// takes to assemble.
///
/// <para>Each snapshot is assembled when the test says, on the test's own thread, and the clock
/// moves only when the test moves it, so how long a snapshot takes is set exactly rather than raced
/// for. A snapshot is a pass over every node found so far, and on a full drive it can take longer
/// than the interval between them; that is the case under test.</para>
/// </summary>
public sealed class WalkSnapshotsTests
{
    private const string Root = @"C:\Users\testuser";

    private readonly ManualTimeProvider _clock = new();
    private readonly ExploreTreeBuilder _builder = new(Root);
    private readonly Queue<(Action Assemble, TaskCompletionSource Done)> _waiting = new();

    private int _started;
    private long _startedAt;

    public WalkSnapshotsTests() =>
        _builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 10),
        ]);

    /// <summary>
    /// One snapshot at a time. Reports keep coming while one is assembled, each long past the
    /// interval, and none of them starts another: with two at once, the walk's share of the
    /// processor would go to pictures nobody can look at before the next replaces them.
    /// </summary>
    [Fact]
    public void StartsNoSnapshotWhileOneIsStillBeingAssembled()
    {
        var snapshots = Snapshots();

        _clock.Advance(WalkSnapshots.Interval);
        Assert.Null(snapshots.Take());
        Assert.Equal(1, _started);

        for (var report = 0; report < 20; report++)
        {
            _clock.Advance(WalkSnapshots.Interval);
            Assert.Null(snapshots.Take());
        }

        Assert.Equal(1, _started);

        FinishAssembling();

        var snapshot = snapshots.Take();

        Assert.NotNull(snapshot);
        Assert.Equal(ExploreChildOrder.ByName, snapshot.ChildOrder);
        Assert.Equal(2, snapshot.NodeCount);
    }

    /// <summary>
    /// The next snapshot is spaced from the end of the last, by the interval or by a multiple of the
    /// time the last took, whichever is longer. Spaced from its start, as it once was, a snapshot
    /// slower than the interval was followed by the next at once.
    /// </summary>
    [Fact]
    public void SpacesTheNextSnapshotFromTheEndOfTheLastByAMultipleOfItsTime()
    {
        var snapshots = Snapshots();
        var took = WalkSnapshots.Interval * 2;

        _clock.Advance(WalkSnapshots.Interval);
        snapshots.Take();
        _clock.Advance(took);
        FinishAssembling();

        // The ended snapshot is handed over, and one interval from its end is not yet enough.
        _clock.Advance(WalkSnapshots.Interval);
        Assert.NotNull(snapshots.Take());
        Assert.Equal(1, _started);

        _clock.Advance((took * WalkSnapshots.AssemblingTimes) - WalkSnapshots.Interval - TimeSpan.FromTicks(1));
        snapshots.Take();
        Assert.Equal(1, _started);

        _clock.Advance(TimeSpan.FromTicks(1));
        snapshots.Take();
        Assert.Equal(2, _started);
    }

    /// <summary>
    /// A quick snapshot waits only the interval from its end, so a small walk still looks alive.
    /// </summary>
    [Fact]
    public void SpacesAQuickSnapshotByTheIntervalAlone()
    {
        var snapshots = Snapshots();

        _clock.Advance(WalkSnapshots.Interval);
        snapshots.Take();
        FinishAssembling();

        _clock.Advance(WalkSnapshots.Interval - TimeSpan.FromTicks(1));
        snapshots.Take();
        Assert.Equal(1, _started);

        _clock.Advance(TimeSpan.FromTicks(1));
        snapshots.Take();
        Assert.Equal(2, _started);
    }

    /// <summary>
    /// However long a snapshot takes, snapshots take no more than their share of the walk's time:
    /// with each one followed by a wait of <see cref="WalkSnapshots.AssemblingTimes"/> times its
    /// own length, there can be no more of them than that many and one fit into the time elapsed.
    ///
    /// <para>Ten minutes of reports, one each walk interval, with every snapshot taking more than
    /// twice the snapshot interval: the case in which snapshots once followed each other with no
    /// gap, and the walk alternated between a rebuild and a moment of progress.</para>
    /// </summary>
    [Fact]
    public void KeepsSnapshotsToABoundedShareOfTheWalkHoweverLongEachTakes()
    {
        var snapshots = Snapshots();
        var took = TimeSpan.FromSeconds(2);
        var elapsed = TimeSpan.FromMinutes(10);
        var tookTicks = (long)(took.TotalSeconds * _clock.TimestampFrequency);

        for (var now = TimeSpan.Zero; now < elapsed; now += BoundedFileWalk.ProgressInterval)
        {
            _clock.Advance(BoundedFileWalk.ProgressInterval);

            if (_waiting.Count > 0 && _clock.GetTimestamp() - _startedAt >= tookTicks)
            {
                FinishAssembling();
            }

            snapshots.Take();
        }

        var most = (int)(elapsed / (took * (WalkSnapshots.AssemblingTimes + 1))) + 1;

        Assert.InRange(_started, 2, most);
    }

    /// <summary>
    /// A snapshot that fails to assemble fails the walk, at the next report. Dropped instead, the
    /// fault would leave every later picture silently missing.
    /// </summary>
    [Fact]
    public void FailsTheWalkWhereASnapshotFailed()
    {
        var snapshots = new WalkSnapshots(
            _builder, _clock, _ => Task.FromException(new InvalidOperationException("assembling failed")));

        _clock.Advance(WalkSnapshots.Interval);
        snapshots.Take();

        _clock.Advance(BoundedFileWalk.ProgressInterval);
        var thrown = Assert.Throws<InvalidOperationException>(snapshots.Take);

        Assert.Equal("assembling failed", thrown.Message);
    }

    private WalkSnapshots Snapshots() => new(_builder, _clock, Start);

    private Task Start(Action assemble)
    {
        _started++;
        _startedAt = _clock.GetTimestamp();

        var done = new TaskCompletionSource();
        _waiting.Enqueue((assemble, done));

        return done.Task;
    }

    private void FinishAssembling()
    {
        var (assemble, done) = _waiting.Dequeue();
        assemble();
        done.SetResult();
    }
}
