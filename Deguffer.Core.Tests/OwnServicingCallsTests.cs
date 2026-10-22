using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Telling the servicing stack Deguffer's own DISM call woke from one an update started, by when each
/// process was created. The hold on an unfinished update is the same for every provider that asks it,
/// so it is pinned here once rather than through each.
/// </summary>
public sealed class OwnServicingCallsTests
{
    private readonly OwnServicingCalls _calls = new();
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;

    [Fact]
    public async Task AProcessCreatedDuringACallWasStartedByIt()
    {
        DateTimeOffset during = default;

        await _calls.MakeAsync(() => Task.FromResult(during = DateTimeOffset.UtcNow));

        Assert.True(_calls.Started(during));
    }

    [Fact]
    public async Task AProcessCreatedBeforeOrAfterEveryCallWasNot()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _calls.MakeAsync(() => Task.FromResult(0));
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.False(_calls.Started(before));
        Assert.False(_calls.Started(after));
        Assert.False(new OwnServicingCalls().Started(DateTimeOffset.UtcNow));
    }

    /// <summary>A call still running has no end yet, so a process created at any point after it began is its.</summary>
    [Fact]
    public async Task AProcessCreatedWhileACallRunsWasStartedByIt()
    {
        var running = new TaskCompletionSource<int>();
        var call = _calls.MakeAsync(() => running.Task);

        Assert.True(_calls.Started(DateTimeOffset.UtcNow.AddSeconds(1)));

        running.SetResult(0);
        await call;
    }

    /// <summary>A call that fails still ran, and still woke the stack.</summary>
    [Fact]
    public async Task AFailedCallIsRememberedWithItsEnd()
    {
        DateTimeOffset during = default;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _calls.MakeAsync<int>(() =>
        {
            during = DateTimeOffset.UtcNow;
            throw new InvalidOperationException("DISM failed.");
        }));

        Assert.True(_calls.Started(during));
        Assert.False(_calls.Started(DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public async Task TheServicingStackACallWokeHoldsNothing()
    {
        await _calls.MakeAsync(() => Task.FromResult(
            _inspector.WithRunning("TrustedInstaller", DateTimeOffset.UtcNow).WithRunning("TiWorker", DateTimeOffset.UtcNow)));

        Assert.Null(UnfinishedUpdate.HoldsEverything(FakeWindowsServicing.Settled, _inspector, _calls));
    }

    /// <summary>
    /// A removal by path is asked without the record, because nothing serialises it against an update
    /// that reuses the stack Deguffer woke. That stack still holds it.
    /// </summary>
    [Fact]
    public async Task ARemovalByPathIsHeldEvenByTheServicingStackACallWoke()
    {
        await _calls.MakeAsync(() => Task.FromResult(_inspector.WithRunning("TiWorker", DateTimeOffset.UtcNow)));

        Assert.Contains("TiWorker", UnfinishedUpdate.HoldsEverything(FakeWindowsServicing.Settled, _inspector));
    }

    [Fact]
    public async Task AServicingStackCreatedOutsideEveryCallHolds()
    {
        await _calls.MakeAsync(() => Task.FromResult(0));
        _inspector.WithRunning("TiWorker", DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.Contains("TiWorker", UnfinishedUpdate.HoldsEverything(FakeWindowsServicing.Settled, _inspector, _calls));
    }

    /// <summary>A creation time that could not be read is not evidence the call started the process.</summary>
    [Fact]
    public async Task AServicingStackOfUnknownStartHolds()
    {
        await _calls.MakeAsync(() => Task.FromResult(_inspector.WithRunning("TrustedInstaller")));

        Assert.Contains("TrustedInstaller", UnfinishedUpdate.HoldsEverything(FakeWindowsServicing.Settled, _inspector, _calls));
    }

    /// <summary>
    /// Only the servicing stack is ever started by a DISM call. Windows' setup engine is an upgrade, and
    /// holds whenever it was created.
    /// </summary>
    [Fact]
    public async Task TheSetupEngineHoldsEvenWhenItStartedDuringACall()
    {
        await _calls.MakeAsync(() => Task.FromResult(_inspector.WithRunning("SetupHost", DateTimeOffset.UtcNow)));

        Assert.Contains("SetupHost", UnfinishedUpdate.HoldsEverything(FakeWindowsServicing.Settled, _inspector, _calls));
    }

    /// <summary>A restart owed holds whatever the servicing stack is doing.</summary>
    [Fact]
    public async Task ARestartOwedHoldsWhateverTheCallsWoke()
    {
        await _calls.MakeAsync(() => Task.FromResult(_inspector.WithRunning("TiWorker", DateTimeOffset.UtcNow)));

        Assert.NotNull(UnfinishedUpdate.HoldsEverything(new FakeWindowsServicing { IsRestartPending = true }, _inspector, _calls));
    }
}
