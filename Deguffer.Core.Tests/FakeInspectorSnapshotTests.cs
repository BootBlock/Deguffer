using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The process and live-tree fakes keep the real inspectors' snapshot contract, which every test of a
/// check at the clean leans on: a fake that answered live would let a check stop reading the machine
/// again with no test going red.
/// </summary>
public sealed class FakeInspectorSnapshotTests
{
    private const string Folder = @"C:\Users\testuser\src\App";

    [Fact]
    public void AProgramStartedAfterTheFirstQueryIsSeenOnlyOnceTheSnapshotIsDropped()
    {
        var inspector = FakeProcessInspector.NothingRunning;
        Assert.Empty(inspector.FindRunning(["tool"]));

        inspector.WithRunning("tool");
        Assert.Empty(inspector.FindRunning(["tool"]));

        inspector.Invalidate();
        Assert.Equal(["tool"], inspector.FindRunning(["tool"]));
    }

    [Fact]
    public void ASnapshotWithinTheLimitIsKeptAndOnePastItIsDropped()
    {
        var inspector = FakeProcessInspector.NothingRunning;
        Assert.Empty(inspector.FindRunning(["tool"]));
        inspector.WithRunning("tool");

        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);
        Assert.Empty(inspector.FindRunning(["tool"]));

        inspector.PassTheReadingLimit().DiscardOlderThan(IUseCheck.ReadingLimit);
        Assert.Equal(["tool"], inspector.FindRunning(["tool"]));
    }

    /// <summary>
    /// The real inspector takes the ids from its snapshot and probes each one at the call, so a process
    /// that has ended since is not reported, and one started since is not found.
    /// </summary>
    [Fact]
    public void ProcessesWithTheirStartAreTheSnapshotsStillRunningNow()
    {
        var started = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var inspector = new FakeProcessInspector("service").WithRunning("TiWorker", started);
        Assert.Equal(2, inspector.FindRunningProcesses(["service", "TiWorker", "late"]).Count);

        inspector.WithoutRunning("service").WithoutRunning("TiWorker").WithRunning("late");

        Assert.Empty(inspector.FindRunningProcesses(["service", "TiWorker", "late"]));
    }

    [Fact]
    public void WhereProgramsAreComesFromTheSnapshotAndHeldFilesAreAskedLive()
    {
        var lockFile = Path.Combine(Folder, "app.lock");
        var inspector = FakeLiveTreeInspector.NothingLive;
        LiveTreeQuery[] asked = [new(Folder, Folder, ["app.lock"])];
        Assert.Empty(inspector.FindLive(asked).Live);

        inspector.WithProgram("editor", workingDirectory: Folder).WithHeldFile(lockFile, "indexer");

        Assert.Equal(["indexer has it open"], Assert.Single(inspector.FindLive(asked).Live).Holders);
        Assert.Empty(inspector.FindOccupiedDirectories().Live);

        inspector.PassTheReadingLimit().DiscardOlderThan(IUseCheck.ReadingLimit);

        Assert.Contains("editor is working in it", Assert.Single(inspector.FindOccupiedDirectories().Live).Holders);
    }

    [Fact]
    public void ATableThatBecameUnreadableIsSeenOnlyOnceTheSnapshotIsDropped()
    {
        var inspector = FakeLiveTreeInspector.NothingLive;
        Assert.True(inspector.FindOccupiedDirectories().Complete);

        inspector.CannotTellFromNow();
        Assert.True(inspector.FindOccupiedDirectories().Complete);

        inspector.Invalidate();
        Assert.False(inspector.FindOccupiedDirectories().Complete);
    }
}
