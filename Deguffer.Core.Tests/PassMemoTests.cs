using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// A provider's cache is read from several threads in one planning pass, so an answer that costs a walk
/// or a tool's run is worked out once however many ask at once, and every one of them is handed it.
/// </summary>
public sealed class PassMemoTests
{
    private const int Askers = 8;

    [Fact]
    public async Task ThreadsAskingAtOnceShareOneAnswerWorkedOutOnce()
    {
        var memo = new PassMemo<object>();
        var worked = 0;
        using var together = new Barrier(Askers);

        var answers = await Task.WhenAll(Enumerable.Range(0, Askers).Select(_ => Task.Factory.StartNew(
                () =>
                {
                    together.SignalAndWait();

                    return memo.Get(() =>
                    {
                        Interlocked.Increment(ref worked);

                        // Long enough for every other asker to arrive while the work runs.
                        Thread.Sleep(100);
                        return new object();
                    });
                },
                TaskCreationOptions.LongRunning)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, worked);
        Assert.All(answers, answer => Assert.Same(answers[0], answer));
    }

    [Fact]
    public void AForgottenAnswerIsWorkedOutAgain()
    {
        var memo = new PassMemo<object>();
        var first = memo.Get(() => new object());

        memo.Forget();

        Assert.NotSame(first, memo.Get(() => new object()));
    }

    /// <summary>A null answer is not remembered, as the field this replaces did not remember one.</summary>
    [Fact]
    public void ANullAnswerIsAskedAgain()
    {
        var memo = new PassMemo<object?>();
        var asked = 0;

        memo.Get(() =>
        {
            asked++;
            return null;
        });
        memo.Get(() =>
        {
            asked++;
            return null;
        });

        Assert.Equal(2, asked);
    }
}
