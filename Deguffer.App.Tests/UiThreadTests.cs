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
