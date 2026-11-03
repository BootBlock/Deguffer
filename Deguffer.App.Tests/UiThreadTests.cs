namespace Deguffer.App.Tests;

/// <summary>
/// The helper every asynchronous view-model test stands on, held to the one promise it makes: that a
/// callback reported from the background arrives on the test's own thread, in order.
/// </summary>
public class UiThreadTests
{
    [Fact]
    public void ProgressReportedFromTheBackgroundArrivesOnTheTestThreadInOrder()
    {
        var thread = Environment.CurrentManagedThreadId;
        var seen = new List<(int Value, int Thread)>();

        UiThread.Run(async () =>
        {
            var progress = new Progress<int>(value => seen.Add((value, Environment.CurrentManagedThreadId)));

            await Task.Run(() =>
            {
                for (var value = 0; value < 50; value++)
                {
                    ((IProgress<int>)progress).Report(value);
                }
            });
        });

        Assert.Equal(Enumerable.Range(0, 50), seen.Select(entry => entry.Value));
        Assert.All(seen, entry => Assert.Equal(thread, entry.Thread));
    }

    /// <summary>
    /// Work a failed test left running posts after the test has ended. Throwing there throws on a
    /// thread-pool thread, which ends the test host and every result with it.
    /// </summary>
    [Fact]
    public void APostAfterTheTestHasEndedDoesNotThrow()
    {
        SynchronizationContext? captured = null;
        UiThread.Run(() =>
        {
            captured = SynchronizationContext.Current;
            return Task.CompletedTask;
        });

        Assert.Null(Record.Exception(() => captured!.Post(_ => { }, null)));
    }

    /// <summary>
    /// Waiting for a post ends on the continuation an answer finished elsewhere posts, and leaves it
    /// unrun, so the test can act before that answer is told.
    /// </summary>
    [Fact]
    public void WaitingForAPostEndsOnOneAndRunsNothing() => UiThread.Run(async () =>
    {
        using var release = new ManualResetEventSlim();
        var answered = false;
        var answering = Task.Run(() => release.Wait(TimeSpan.FromSeconds(30)));

        var told = Told();

        async Task Told()
        {
            await answering;
            answered = true;
        }

        release.Set();

        Assert.True(UiThread.WaitForPost(TimeSpan.FromSeconds(30)));
        Assert.True(answering.IsCompleted);
        Assert.False(answered);

        await told;

        Assert.True(answered);
    });

    /// <summary>
    /// A post already waiting when the wait begins is not what it waits for, or a test would act
    /// before the answer it means had been finished.
    /// </summary>
    [Fact]
    public void WaitingForAPostIgnoresOneMadeBeforeIt() => UiThread.Run(() =>
    {
        SynchronizationContext.Current!.Post(_ => { }, null);

        Assert.False(UiThread.WaitForPost(TimeSpan.FromMilliseconds(100)));

        return Task.CompletedTask;
    });

    [Fact]
    public void AFailureInTheBodyReachesTheTest()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => UiThread.Run(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("from the body");
        }));

        Assert.Equal("from the body", failure.Message);
    }
}
