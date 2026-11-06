using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// DISM's analysis of the component store, started when an administrator's planning pass starts
/// rather than when the component store's rows reach a slot near the end of the planner's list.
///
/// <para>Proved by the order things happen in, never by how long they took: the providers ahead of the
/// rows each wait for the analysis to start, and fill every slot the planner has while they wait.</para>
/// </summary>
public sealed class ComponentStoreEarlyStartTests : IDisposable
{
    /// <summary>Far longer than a fake DISM takes to start, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>How long a pass that does not wait for DISM is given to return before DISM exits.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeWindowsServicing _servicing = FakeWindowsServicing.Settled;
    private readonly string _store;

    public ComponentStoreEarlyStartTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).AsAdministrator();
        _system = new FakeSystemDirectories(_temp.Path);
        _temp.CreateFile(4096, "Windows", "WinSxS", "Manifests", "component.manifest");
        _store = Path.Combine(_system.WindowsDirectory, "WinSxS");
    }

    public void Dispose() => _temp.Dispose();

    private string Dism => NativeSystemTool.In(_system, "Dism.exe");

    /// <summary>Both component store rows over one analysis, as the planner builds them.</summary>
    private ICleanupProvider[] Rows(IProcessRunner runner, FakeUserEnvironment? environment = null)
    {
        environment ??= _environment;
        var analysis = new ComponentStoreAnalysis(_system, runner, new OwnServicingCalls());

        return
        [
            new ComponentStoreCleanupProvider(environment, runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis),
            new ComponentStoreResetBaseProvider(environment, runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis),
        ];
    }

    /// <summary>
    /// Enough providers ahead of the rows to take every slot, each finishing only once the analysis has
    /// started, and then running <paramref name="then"/>. Started at the rows' own turn, the analysis
    /// would wait for a slot none of them gives up.
    /// </summary>
    private static FakeCleanupProvider[] Ahead(Task analysisStarted, Action? then = null) =>
    [
        .. Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce).Select(i => new FakeCleanupProvider($"ahead-{i}")
        {
            WhilePlanning = () =>
            {
                if (!analysisStarted.Wait(Deadline))
                {
                    throw new TimeoutException("The analysis had not started while the providers ahead of its rows planned.");
                }

                then?.Invoke();
            },
        }),
    ];

    /// <summary>
    /// The rows stand last, behind a full set of slots, and DISM still starts before any provider ahead
    /// of them finishes. It is asked once for both rows, and both plan from its answer.
    /// </summary>
    [Fact]
    public async Task AnAdministratorsPassStartsTheAnalysisBeforeTheProvidersAheadOfItFinish()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner().Replying(Dism, arguments =>
        {
            started.TrySetResult();
            return new CommandOutcome(0, ComponentStoreReportTests.Sample, string.Empty);
        });

        var rows = Rows(runner);
        var planner = new CleanupPlanner([.. Ahead(started.Task), .. rows]);

        var findings = await planner.PlanAllAsync().WaitAsync(Deadline);

        var asked = Assert.Single(runner.Invocations);
        Assert.Equal(ComponentStoreAnalysis.AnalyzeArguments, asked.Arguments);
        Assert.All(
            rows,
            row => Assert.Single(Assert.Single(findings, finding => finding.Provider == row).Plan!.Steps));
    }

    /// <summary>
    /// The analysis waits on the servicing stack, so it is not started early while an update holds the
    /// store, as the row's own plan does not start it then either.
    /// </summary>
    [Fact]
    public async Task NoAnalysisStartsWhileAnUpdateHoldsTheStore()
    {
        _servicing.IsRestartPending = true;
        var runner = new FakeProcessRunner();
        var planner = new CleanupPlanner(Rows(runner));

        var findings = await planner.PlanAllAsync();

        Assert.Empty(runner.Invocations);
        Assert.All(findings, finding => Assert.True(finding.Plan!.WaitsForAnUpdate));
    }

    /// <summary>
    /// DISM refuses any other account at once, so nothing is started early for one. The row's own turn
    /// still asks, and is told to elevate.
    /// </summary>
    [Fact]
    public void AnAccountThatIsNotAnAdministratorStartsNothingEarly()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var runner = new FakeProcessRunner();
        var row = new ComponentStoreCleanupProvider(environment, runner, FakeProcessInspector.NothingRunning, _system, _servicing);

        row.BeginPass(CancellationToken.None);

        Assert.Empty(runner.Invocations);
    }

    /// <summary>A Windows that keeps no component store has nothing to analyse.</summary>
    [Fact]
    public void AWindowsWithNoComponentStoreStartsNothingEarly()
    {
        Directory.Delete(_store, recursive: true);
        var runner = new FakeProcessRunner();

        foreach (var row in Rows(runner))
        {
            row.BeginPass(CancellationToken.None);
        }

        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// #158: a preview the user cancels ends the analysis the pass started, before any row reached a
    /// slot, and the pass returns only once DISM has exited, so a scan started straight after it cannot
    /// run a second analysis beside it.
    /// </summary>
    [Fact]
    public async Task ACancelledPassEndsTheAnalysisItStartedBeforeItReturns()
    {
        var dism = new SlowToExitDism();
        using var cts = new CancellationTokenSource();
        var planner = new CleanupPlanner([.. Ahead(dism.Started, cts.Cancel), .. Rows(dism)]);

        var pass = planner.PlanAllAsync(ct: cts.Token);
        var returnedEarly = await ReturnsBeforeDismExits(pass, dism);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.WaitAsync(Deadline));
        Assert.False(returnedEarly, "the pass returned while DISM was still exiting");
    }

    /// <summary>
    /// A pass that fails before the rows reach a slot ends the analysis it started too, and returns
    /// only once DISM has exited. Nothing else would cancel it: the failure stops only the slots.
    /// </summary>
    [Fact]
    public async Task APassThatFailsEndsTheAnalysisItStartedBeforeItReturns()
    {
        var dism = new SlowToExitDism();
        var ahead = Ahead(dism.Started);
        ahead[0].PlanFailure = new InvalidOperationException("The plan failed.");
        var planner = new CleanupPlanner([.. ahead, .. Rows(dism)]);

        var pass = planner.PlanAllAsync();
        var returnedEarly = await ReturnsBeforeDismExits(pass, dism);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.WaitAsync(Deadline));
        Assert.False(returnedEarly, "the pass returned while DISM was still exiting");
    }

    /// <summary>
    /// A plan that fails while the rows wait on the analysis the pass started ends the analysis, and
    /// the pass returns once DISM has exited. Stopping only the slots would leave the rows waiting on
    /// DISM's answer, and the pass open until it came.
    /// </summary>
    [Fact]
    public async Task APlanThatFailsWhileTheRowsWaitEndsTheAnalysisBeforeThePassReturns()
    {
        var dism = new SlowToExitDism();
        var broken = new FakeCleanupProvider("broken")
        {
            WhilePlanning = () => dism.Started.Wait(Deadline),
            PlanFailure = new InvalidOperationException("The plan failed."),
        };

        var planner = new CleanupPlanner([broken, .. Rows(dism)]);

        var pass = planner.PlanAllAsync();
        var returnedEarly = await ReturnsBeforeDismExits(pass, dism);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pass.WaitAsync(Deadline));
        Assert.False(returnedEarly, "the pass returned while DISM was still exiting");
    }

    /// <summary>
    /// An analysis a row starts at its own turn, as for an account that is not an administrator, is
    /// waited out too: a cancelled pass returns only once DISM has exited.
    /// </summary>
    [Fact]
    public async Task AnAnalysisARowStartedEndsBeforeACancelledPassReturns()
    {
        var dism = new SlowToExitDism();
        using var cts = new CancellationTokenSource();
        var cancelling = new FakeCleanupProvider("cancelling")
        {
            WhilePlanning = () =>
            {
                if (dism.Started.Wait(Deadline))
                {
                    cts.Cancel();
                }
            },
        };

        var planner = new CleanupPlanner([cancelling, .. Rows(dism, new FakeUserEnvironment(_temp.Path))]);

        var pass = planner.PlanAllAsync(ct: cts.Token);
        var returnedEarly = await ReturnsBeforeDismExits(pass, dism);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.WaitAsync(Deadline));
        Assert.False(returnedEarly, "the pass returned while DISM was still exiting");
    }

    /// <summary>
    /// Whether <paramref name="pass"/> returned once DISM was told to stop and before it exited, after
    /// which DISM is let exit. A pass that waits for DISM cannot return in that window, so this never
    /// reports early for one. One that does not wait returns at once, well inside <see cref="Settle"/>.
    /// </summary>
    private static async Task<bool> ReturnsBeforeDismExits(Task pass, SlowToExitDism dism)
    {
        await dism.Cancelled.WaitAsync(Deadline);
        var returnedEarly = await Task.WhenAny(pass, Task.Delay(Settle)) == pass;
        dism.Exit();

        return returnedEarly;
    }

    /// <summary>
    /// DISM that never answers, and once its run is cancelled, exits only when the test lets it, as the
    /// real one takes a moment to end after it is told to.
    /// </summary>
    private sealed class SlowToExitDism : IProcessRunner
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        /// <summary>Completed once the run was told to stop.</summary>
        public Task Cancelled => _cancelled.Task;

        public void Exit() => _exit.TrySetResult();

        public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
        {
            _started.TrySetResult();

            await using (ct.Register(() => _cancelled.TrySetResult()))
            {
                await _cancelled.Task.ConfigureAwait(false);
            }

            await _exit.Task.ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
    }
}
