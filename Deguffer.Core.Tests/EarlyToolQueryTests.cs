using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// #450: the tool queries the providers' plans wait on start as the planning pass begins, outside the
/// planning slots, and no more of them run at once than <see cref="PassStart.ToolsAtOnce"/>.
///
/// <para>Proved by the order things happen in, never by how long they took: the providers ahead of the
/// row each wait for its query to start, and fill every slot the planner has while they wait.</para>
/// </summary>
public sealed class EarlyToolQueryTests : IDisposable
{
    /// <summary>Far longer than a fake tool takes to start, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EarlyToolQueryTests() => _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("npm");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// npm stands last, behind a full set of slots whose providers each finish only once npm has been
    /// asked, and it is still asked while they plan. Asked at its own turn, the query would wait for a
    /// slot none of them gives up. It is asked once, and the row plans from its answer.
    /// </summary>
    [Fact]
    public async Task AToolRowsQueryStartsBeforeTheProvidersAheadOfItFinish()
    {
        var npm = _environment.FindExecutable("npm")!;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relocated = _temp.CreateDirectory("elsewhere", "npm-cache");
        _temp.CreateFile(2048, "elsewhere", "npm-cache", "_cacache", "content", "blob");

        var runner = new FakeProcessRunner().Replying(npm, arguments =>
        {
            if (arguments.Contains("config get cache", StringComparison.Ordinal))
            {
                asked.TrySetResult();
            }

            return new CommandOutcome(0, relocated, string.Empty);
        });

        var row = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);
        var planner = new CleanupPlanner([.. Ahead(asked.Task), row]);

        var findings = await planner.PlanAllAsync().WaitAsync(Deadline);

        Assert.Single(runner.Invocations, run => run.Arguments.Contains("config get cache", StringComparison.Ordinal));
        var step = Assert.IsType<RunCommandStep>(Assert.Single(Assert.Single(findings, finding => finding.Provider == row).Plan!.Steps));
        Assert.Equal([Path.Combine(relocated, "_cacache")], step.MeasuredPaths);
    }

    /// <summary>
    /// One more provider than the bound, each starting a query held open as the pass begins: as many run
    /// as the bound allows, and the last waits for a place rather than starting in the same instant.
    /// </summary>
    [Fact]
    public async Task NoMoreQueriesStartedAsThePassBeginsRunAtOnceThanTheBound()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var begun = new SemaphoreSlim(0);
        var running = 0;
        var heldAtOnce = 0;

        async Task Query(CancellationToken ct)
        {
            Interlocked.Increment(ref running);
            begun.Release();

            try
            {
                await release.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var watcher = new FakeCleanupProvider("watcher")
        {
            WhilePlanning = () =>
            {
                for (var i = 0; i < PassStart.ToolsAtOnce; i++)
                {
                    if (!begun.Wait(Deadline))
                    {
                        throw new TimeoutException($"Only {i} of {PassStart.ToolsAtOnce} early queries started.");
                    }
                }

                // Every place is held, so a query beyond the bound could only start by taking one it lacks.
                if (begun.Wait(TimeSpan.FromMilliseconds(200)))
                {
                    throw new InvalidOperationException("An early query started beyond the bound.");
                }

                heldAtOnce = Volatile.Read(ref running);
                release.SetResult();
            },
        };

        var querying = Enumerable.Range(0, PassStart.ToolsAtOnce + 1)
            .Select(i => new FakeCleanupProvider($"tool-{i}") { EarlyQuery = Query });

        await new CleanupPlanner([watcher, .. querying]).PlanAllAsync().WaitAsync(Deadline);

        Assert.Equal(PassStart.ToolsAtOnce, heldAtOnce);
        Assert.Equal(0, Volatile.Read(ref running));
    }

    /// <summary>
    /// A cancelled pass ends every query it started, the ones still waiting for a place included, before
    /// the pass returns, so none is still at work beside the next pass's own.
    /// </summary>
    [Fact]
    public async Task ACancelledPassEndsEveryQueryItStartedBeforeItReturns()
    {
        using var cts = new CancellationTokenSource();
        var begun = new SemaphoreSlim(0);
        var running = 0;

        async Task Query(CancellationToken ct)
        {
            Interlocked.Increment(ref running);
            begun.Release();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var canceller = new FakeCleanupProvider("canceller")
        {
            WhilePlanning = () =>
            {
                if (!begun.Wait(Deadline))
                {
                    throw new TimeoutException("No early query started.");
                }

                cts.Cancel();
            },
        };

        var querying = Enumerable.Range(0, PassStart.ToolsAtOnce + 1)
            .Select(i => new FakeCleanupProvider($"tool-{i}") { EarlyQuery = Query });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CleanupPlanner([canceller, .. querying]).PlanAllAsync(ct: cts.Token).WaitAsync(Deadline));

        Assert.Equal(0, Volatile.Read(ref running));
    }

    /// <summary>
    /// Enough providers ahead of the row to take every slot, each finishing only once its query has
    /// started.
    /// </summary>
    private static FakeCleanupProvider[] Ahead(Task queryStarted) =>
    [
        .. Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce).Select(i => new FakeCleanupProvider($"ahead-{i}")
        {
            WhilePlanning = () =>
            {
                if (!queryStarted.Wait(Deadline))
                {
                    throw new TimeoutException("The row's query had not started while the providers ahead of it planned.");
                }
            },
        }),
    ];
}
