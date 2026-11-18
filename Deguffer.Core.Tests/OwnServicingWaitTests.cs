using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A step held while Windows is updating, run while the servicing stack a component store command of
/// Deguffer's own woke is still running. Windows keeps that stack running for minutes after the
/// command returns, and a preview runs the command seconds before the clean, so a step held by it
/// would never run straight after a preview. It waits for that stack to stop instead, and is held by
/// any other.
///
/// <para>Driven against a plan built here, with the machine's servicing, its processes and the clock
/// all fakes, so nothing waits in real time and nothing reaches the real servicing stack.</para>
/// </summary>
public sealed class OwnServicingWaitTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();
    private readonly FakeWindowsServicing _servicing = FakeWindowsServicing.Settled;
    private readonly FakeProcessInspector _inspector = FakeProcessInspector.NothingRunning;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDiskCleanupHandlers _handlers = FakeDiskCleanupHandlers.DoingNothing();

    public void Dispose() => _temp.Dispose();

    private string Staging => Path.Combine(_temp.Path, "$Windows.~WS");

    private CleanupPlan Plan() => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableWithCost,
        WhatHappensOnNextUse = "Nothing.",
        Steps =
        [
            new DiskCleanupStep(Staging, "Installation files")
            {
                Handler = "Windows ESD installation files",
                Volume = _temp.Path,
                Estimated = new ScanSize(4096, 4096),
                HeldWhileUpdating = true,
            },
        ],
    };

    private Task<CleanupResult> ExecuteAsync(CancellationToken ct = default) =>
        Executor().ExecuteAsync(Prepared(), runReach: null, residue: null, progress: null, underway: null, ct);

    /// <summary>The plan, with its folder on the disk.</summary>
    private CleanupPlan Prepared()
    {
        Directory.CreateDirectory(Staging);
        File.WriteAllBytes(Path.Combine(Staging, "install.esd"), new byte[4096]);
        return Plan();
    }

    /// <summary>An executor on the test's machine, as each provider builds one of its own.</summary>
    private PlanExecutor Executor() => new(
        new FakeProcessRunner(),
        ParallelEnumerationScanner.Default,
        RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
        handlers: _handlers,
        servicing: _servicing,
        inspector: _inspector,
        time: _time);

    /// <summary>The servicing stack, woke by a DISM call of Deguffer's own, as the analysis wakes it.</summary>
    private Task WakeTheStackOurselvesAsync() =>
        OwnServicingCalls.For(_servicing).MakeAsync(() =>
        {
            _inspector.WithRunning("TrustedInstaller", DateTimeOffset.UtcNow).WithRunning("TiWorker", DateTimeOffset.UtcNow);
            return Task.FromResult(0);
        });

    /// <summary>
    /// The step waits while only Deguffer's own stack runs, and Windows' cleanup runs once it stops.
    /// </summary>
    [Fact]
    public async Task WaitsForTheStackItsOwnCommandWokeAndThenRuns()
    {
        await WakeTheStackOurselvesAsync();

        var run = ExecuteAsync();
        await _time.WhenWaitingAsync(Patience);

        Assert.False(run.IsCompleted);
        Assert.Empty(_handlers.Calls);

        _inspector.WithoutRunning("TiWorker").WithoutRunning("TrustedInstaller");
        _time.Advance(TimeSpan.FromSeconds(2));

        var result = await run.WaitAsync(Patience);

        Assert.Single(_handlers.Calls);
        Assert.DoesNotContain(result.Steps, step => step.Message?.Contains("servicing stack", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// A stack that outlasts the wait may be doing an update's work, so the step is held and Windows'
    /// cleanup is never asked.
    /// </summary>
    [Fact]
    public async Task HoldsTheStepWhereItsOwnStackOutlastsTheWait()
    {
        await WakeTheStackOurselvesAsync();

        var run = ExecuteAsync();

        for (var waited = TimeSpan.Zero; !run.IsCompleted && waited <= UpdateHoldAtClean.OwnServicingWait; waited += TimeSpan.FromSeconds(2))
        {
            await Task.WhenAny(run, _time.WhenWaitingAsync(Patience));
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        var outcome = Assert.Single((await run.WaitAsync(Patience)).Steps);

        Assert.False(outcome.Succeeded);
        Assert.Contains("still running after 10 minutes", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_handlers.Calls);
        Assert.True(Directory.Exists(Staging));
    }

    /// <summary>
    /// The wait is for the session, not for each row: once it has outlasted the wait, the next row's
    /// step is held at once, through another provider's executor, rather than waiting all over again.
    /// </summary>
    [Fact]
    public async Task ARowAfterTheWaitRanOutIsHeldWithoutWaitingAgain()
    {
        await WakeTheStackOurselvesAsync();

        var first = ExecuteAsync();

        for (var waited = TimeSpan.Zero; !first.IsCompleted && waited <= UpdateHoldAtClean.OwnServicingWait; waited += TimeSpan.FromSeconds(2))
        {
            await Task.WhenAny(first, _time.WhenWaitingAsync(Patience));
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        await first.WaitAsync(Patience);

        var outcome = Assert.Single((await ExecuteAsync().WaitAsync(Patience)).Steps);

        Assert.Contains("still running after 10 minutes", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _time.Waiting);
        Assert.Empty(_handlers.Calls);
    }

    /// <summary>
    /// A servicing stack nothing of Deguffer's woke is an update's, and holds the step at once.
    /// </summary>
    [Fact]
    public async Task AStackItDidNotWakeHoldsTheStepWithoutWaiting()
    {
        _inspector.WithRunning("TiWorker", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        var outcome = Assert.Single((await ExecuteAsync().WaitAsync(Patience)).Steps);

        Assert.False(outcome.Succeeded);
        Assert.Contains("installing an update now", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, _time.Waiting);
        Assert.Empty(_handlers.Calls);
    }

    /// <summary>
    /// An update that starts its own stack while the step waits for Deguffer's holds the step once
    /// Deguffer's has stopped.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatStartsDuringTheWaitHoldsTheStep()
    {
        await WakeTheStackOurselvesAsync();

        var run = ExecuteAsync();
        await _time.WhenWaitingAsync(Patience);

        _inspector.WithoutRunning("TiWorker").WithoutRunning("TrustedInstaller").WithRunning("SetupHost").PassTheReadingLimit();
        _time.Advance(TimeSpan.FromSeconds(2));

        var outcome = Assert.Single((await run.WaitAsync(Patience)).Steps);

        Assert.False(outcome.Succeeded);
        Assert.Contains("SetupHost", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_handlers.Calls);
    }

    /// <summary>A clean cancelled while it waits stops without asking Windows' cleanup.</summary>
    [Fact]
    public async Task ACleanCancelledWhileItWaitsStops()
    {
        await WakeTheStackOurselvesAsync();
        using var cancel = new CancellationTokenSource();

        var run = ExecuteAsync(cancel.Token);
        await _time.WhenWaitingAsync(Patience);
        await cancel.CancelAsync();

        var result = await run.WaitAsync(Patience);

        Assert.True(result.Interrupted);
        Assert.Empty(_handlers.Calls);
        Assert.True(Directory.Exists(Staging));
    }
}
