using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// A crew has every helper its bound allows at work at once from the start. The walk and the read of
/// the file table both start their helpers here, so this holds for both, where
/// <see cref="WalkThreadCountTests"/> shows it through the walk.
/// </summary>
public sealed class WorkerCrewTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void EveryHelperTheBoundAllowsIsAtWorkAtOnce()
    {
        const int Threads = 64;
        const int Helpers = Threads - 1;
        using var allAtWork = new CountdownEvent(Helpers);
        using var released = new ManualResetEventSlim();
        var claimed = 0;

        var crew = new WorkerCrew(
            Threads,
            () => () =>
            {
                allAtWork.Signal();
                released.Wait();
            },
            () => Volatile.Read(ref claimed) < Helpers);

        var started = 0;
        while (crew.TryStartHelper())
        {
            started++;
            Interlocked.Increment(ref claimed);
        }

        var reached = allAtWork.Wait(Deadline);
        var atWork = Helpers - allAtWork.CurrentCount;
        released.Set();
        crew.Finish();

        Assert.Equal(Helpers, started);
        Assert.True(reached, $"{atWork} of {Helpers} helpers were at work {Deadline.TotalMilliseconds} ms after they were started.");
    }
}
