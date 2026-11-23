using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the checks at the clean share the question for one reading: built once however many ask at
/// once, built outside the source's gate, and built again where the build failed or was stopped for
/// the check that started it.
/// </summary>
public sealed class LiveTreeQuestionSourceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly LiveTreeReading First = new(1);

    private static readonly LiveTreeReading Second = new(2);

    // Released by every test before it ends, so a build held in flight never outlives it.
    private readonly ManualResetEventSlim _release = new();
    private readonly ManualResetEventSlim _building = new();

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
        _building.Dispose();
    }

    /// <summary>
    /// Two checks asking from one reading while its build is under way wait for that build, so the
    /// question is built once between them.
    /// </summary>
    [Fact]
    public async Task ChecksAskingAtOnceFromOneReadingShareOneBuild()
    {
        var built = 0;
        var finish = new TaskCompletionSource<LiveTreeQuestion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new LiveTreeQuestionSource(_ =>
        {
            Interlocked.Increment(ref built);
            return finish.Task;
        });

        var first = source.ForAsync(First, CancellationToken.None);
        var second = source.ForAsync(First, CancellationToken.None);
        finish.SetResult(Question());

        Assert.Same(await first.WaitAsync(Patience), await second.WaitAsync(Patience));
        Assert.Equal(1, built);
    }

    /// <summary>
    /// The rule runs outside the gate. A build held before its first await, as a rule that reads the
    /// disk first is, leaves a check asking from a newer reading free to build its own. Built under
    /// the gate, that check waits for the held build and never gets its answer.
    /// </summary>
    [Fact]
    public async Task ABuildUnderWayHoldsNoOtherCheck()
    {
        var source = new LiveTreeQuestionSource(_ =>
        {
            if (!_building.IsSet)
            {
                _building.Set();
                _release.Wait(Patience);
            }

            return Task.FromResult(Question());
        });

        var held = Task.Run(() => source.ForAsync(First, CancellationToken.None));
        Assert.True(_building.Wait(Patience));

        var newer = Task.Run(() => source.ForAsync(Second, CancellationToken.None));
        var finished = await Task.WhenAny(newer, Task.Delay(Patience));

        _release.Set();
        Assert.Same(newer, finished);
        await held.WaitAsync(Patience);
    }

    /// <summary>
    /// A build that failed is no answer about the machine, so the next check builds again rather than
    /// being handed the failure for the rest of the reading.
    /// </summary>
    [Fact]
    public async Task AFailedBuildIsNotKept()
    {
        var built = 0;
        var source = new LiveTreeQuestionSource(_ => Interlocked.Increment(ref built) == 1
            ? Task.FromException<LiveTreeQuestion>(new IOException("The solution could not be read."))
            : Task.FromResult(Question()));

        await Assert.ThrowsAsync<IOException>(() => source.ForAsync(First, CancellationToken.None));

        Assert.NotNull(await source.ForAsync(First, CancellationToken.None));
        Assert.Equal(2, built);
    }

    /// <summary>
    /// A build stopped for the check that started it is built again for a check still asking, which
    /// would otherwise be told it was cancelled when it was not.
    /// </summary>
    [Fact]
    public async Task ABuildCancelledForItsStarterIsBuiltAgainForAnotherCheck()
    {
        using var starter = new CancellationTokenSource();
        var built = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new LiveTreeQuestionSource(async ct =>
        {
            if (Interlocked.Increment(ref built) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            ct.ThrowIfCancellationRequested();
            return Question();
        });

        var cancelled = source.ForAsync(First, starter.Token);
        await started.Task.WaitAsync(Patience);
        var waiting = source.ForAsync(First, CancellationToken.None);
        await starter.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));
        Assert.NotNull(await waiting.WaitAsync(Patience));
        Assert.Equal(2, built);
    }

    private static LiveTreeQuestion Question() =>
        new(candidate => new LiveTreeQuery(candidate.Path, candidate.Project));
}
