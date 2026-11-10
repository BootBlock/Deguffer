using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// How the About page's starfield hears that windows moved: a drag raises a move for every frame, and
/// the field asks again whether it is covered once for the burst, not once for each move, and never
/// misses the last.
/// </summary>
public sealed class ThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    /// <summary>Long enough after the interval that a second run, were one coming, would have come.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ABurstOfPokesRunsOnce()
    {
        var runs = 0;
        var throttle = Make(() => Interlocked.Increment(ref runs));

        Poke(throttle, times: 50);

        await Eventually.HoldsAsync(() => Volatile.Read(ref runs) == 1, "the burst ran");
        await Task.Delay(Quiet);

        Assert.Equal(1, Volatile.Read(ref runs));
        GC.KeepAlive(throttle);
    }

    /// <summary>A poke after a run is not swallowed by it: the windows moved again, so the field asks again.</summary>
    [Fact]
    public async Task APokeAfterARunRunsAgain()
    {
        var runs = 0;
        var throttle = Make(() => Interlocked.Increment(ref runs));

        Poke(throttle, times: 1);
        await Eventually.HoldsAsync(() => Volatile.Read(ref runs) == 1, "the first poke ran");

        Poke(throttle, times: 1);
        await Eventually.HoldsAsync(() => Volatile.Read(ref runs) == 2, "the second poke ran");
        GC.KeepAlive(throttle);
    }

    /// <summary>
    /// A window dragged over the field moves for as long as the drag lasts, and the field asks again
    /// during the drag, not only once it ends, so it pauses as soon as it is covered.
    /// </summary>
    [Fact]
    public async Task AStreamOfPokesRunsWhileItLasts()
    {
        var runs = 0;
        var throttle = Make(() => Interlocked.Increment(ref runs));
        var end = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);

        while (DateTime.UtcNow < end)
        {
            Poke(throttle, times: 1);
            await Task.Delay(5);
        }

        Assert.True(Volatile.Read(ref runs) >= 2, $"Ran {runs} times during the stream.");
        GC.KeepAlive(throttle);
    }

    /// <summary>A field unloaded calls off the run it asked for.</summary>
    [Fact]
    public async Task AStoppedThrottleDoesNotRun()
    {
        var runs = 0;
        var throttle = Make(() => Interlocked.Increment(ref runs));

        Poke(throttle, times: 1);
        CompositionThread.Run(() =>
        {
            throttle.Stop();
            return true;
        });
        await Task.Delay(Quiet);

        Assert.Equal(0, Volatile.Read(ref runs));
        GC.KeepAlive(throttle);
    }

    private static Throttle Make(Action action) =>
        CompositionThread.Run(() => new Throttle(CompositionThread.Queue, Interval, action));

    private static void Poke(Throttle throttle, int times) =>
        CompositionThread.Run(() =>
        {
            for (var i = 0; i < times; i++)
            {
                throttle.Poke();
            }

            return true;
        });
}
