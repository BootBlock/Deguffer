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
    private ICleanupProvider[] Rows(IProcessRunner runner)
    {
        var analysis = new ComponentStoreAnalysis(_system, runner, new OwnServicingCalls());

        return
        [
            new ComponentStoreCleanupProvider(_environment, runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis),
            new ComponentStoreResetBaseProvider(_environment, runner, FakeProcessInspector.NothingRunning, _system, _servicing, analysis),
        ];
    }

    /// <summary>
    /// Enough providers ahead of the rows to take every slot, each finishing only once the analysis has
    /// started. Started at the rows' own turn, the analysis would wait for a slot none of them gives up.
    /// </summary>
    private static FakeCleanupProvider[] Ahead(Task analysisStarted) =>
    [
        .. Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce).Select(i => new FakeCleanupProvider($"ahead-{i}")
        {
            WhilePlanning = () =>
            {
                if (!analysisStarted.Wait(Deadline))
                {
                    throw new TimeoutException("The analysis had not started while the providers ahead of its rows planned.");
                }
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
    /// #158: a preview the user cancels ends DISM's analysis, though the pass started it rather than a
    /// row's plan.
    /// </summary>
    [Fact]
    public async Task ACancelledPassEndsTheAnalysisItStarted()
    {
        var dism = new UnansweredDism();
        using var cts = new CancellationTokenSource();
        var ahead = new FakeCleanupProvider("ahead")
        {
            WhilePlanning = () =>
            {
                if (dism.Started.Wait(Deadline))
                {
                    cts.Cancel();
                }
            },
        };

        var planner = new CleanupPlanner([ahead, .. Rows(dism)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.PlanAllAsync(ct: cts.Token).WaitAsync(Deadline));

        Assert.True(await dism.Ended.WaitAsync(Deadline), "DISM was left analysing after the preview was cancelled");
    }

    /// <summary>
    /// A pass that fails ends the analysis it started too, and the rows waiting on it stop waiting
    /// rather than holding the pass open until DISM answers.
    /// </summary>
    [Fact]
    public async Task APassThatFailsEndsTheAnalysisItStarted()
    {
        var dism = new UnansweredDism();
        var broken = new FakeCleanupProvider("broken") { PlanFailure = new InvalidOperationException("The plan failed.") };
        var planner = new CleanupPlanner([broken, .. Rows(dism)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAllAsync().WaitAsync(Deadline));

        Assert.True(await dism.Ended.WaitAsync(Deadline), "DISM was left analysing after the pass failed");
    }

    /// <summary>DISM that never answers, and ends only when its run is cancelled.</summary>
    private sealed class UnansweredDism : IProcessRunner
    {
        private readonly TaskCompletionSource<bool> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        /// <summary>True once the run was cancelled.</summary>
        public Task<bool> Ended => _ended.Task;

        public async Task<CommandOutcome> RunAsync(string fileName, string arguments, ToolRun run, CancellationToken ct)
        {
            _started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                throw new InvalidOperationException("An infinite delay returned.");
            }
            finally
            {
                _ended.TrySetResult(ct.IsCancellationRequested);
            }
        }
    }
}
