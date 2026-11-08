using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="PipelinesAtOnce"/>: every pipeline at once, and the first failure stopping the rest and
/// being what is thrown.
/// </summary>
public sealed class PipelinesAtOnceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The held pipeline waits on nothing but its token, so the run ends only if the failure stops it.
    /// It is listed first, so a run that awaited the pipelines in turn would never reach the failure.
    /// </summary>
    [Fact]
    public async Task OnePipelinesFailureStopsTheOthersAndIsWhatIsThrown()
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;

        var running = PipelinesAtOnce.RunAsync(
            ["held", "failing"],
            async (pipeline, stop) =>
            {
                if (pipeline == "held")
                {
                    holding.SetResult();

                    try
                    {
                        await Task.Delay(Timeout.Infinite, stop);
                    }
                    finally
                    {
                        stopped = stop.IsCancellationRequested;
                    }
                }

                await holding.Task;
                throw new InvalidOperationException("The disk failed.");
            },
            CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(Deadline));

        Assert.Equal("The disk failed.", thrown.Message);
        Assert.True(stopped);
    }

    [Fact]
    public async Task EveryPipelineRunsAtOnce()
    {
        var started = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await PipelinesAtOnce.RunAsync(
            [1, 2, 3],
            async (_, _) =>
            {
                if (Interlocked.Increment(ref started) == 3)
                {
                    all.SetResult();
                }

                await all.Task;
            },
            CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(3, started);
    }
}
