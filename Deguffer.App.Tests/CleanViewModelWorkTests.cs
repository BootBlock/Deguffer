using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Working;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What the Storage page tells the taskbar button. Its scan and its clean each count as work under
/// way for exactly as long as the page's own busy indicator is up, however they end, because a span
/// left counted keeps the taskbar busy over an idle window for the life of the process.
/// </summary>
public class CleanViewModelWorkTests
{
    [Fact]
    public void AScanIsUnderWayWhileItPlansAndEndsWithIt()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        var underWay = false;
        cache.WhilePlanning = () => underWay = !page.Work.IsIdle;

        page.Scan();

        Assert.True(underWay);
        Assert.True(page.Work.IsIdle);
    }

    [Fact]
    public void AFailedScanEndsItsWorkAndSaysItFailed()
    {
        var broken = new FakeCleanupProvider("broken");
        using var page = new StoragePage([broken]);
        broken.PlanFailure = new IOException("The device is not ready.");
        var failures = Failures(page.Work);

        page.Scan();

        Assert.True(page.Work.IsIdle);
        Assert.Equal(1, failures());
    }

    /// <summary>The user asked for a cancellation, so the taskbar is not turned red for it.</summary>
    [Fact]
    public void ACancelledScanEndsItsWorkWithoutAFailure()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.PlanFailure = new OperationCanceledException();
        var failures = Failures(page.Work);

        page.Scan();

        Assert.True(page.Work.IsIdle);
        Assert.Equal(0, failures());
    }

    /// <summary>
    /// The clean says how far it has got as the page's bar does, and the re-plan after it, which
    /// cannot say, takes the measure away again before the work ends.
    /// </summary>
    [Fact]
    public void ACleanFillsTheBarThenEndsItsWork()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        cache.Steps = [page.Cache("a", 1024)];
        cache.Fractions = [0.5, 1];
        page.Scan();
        var seen = new List<double?>();
        page.Work.Changed += (_, _) => seen.Add(page.Work.Current.SingleOrDefault()?.Fraction);
        var failures = Failures(page.Work);

        page.Clean();

        Assert.Equal([null, 0.5, 1, null, null], seen);
        Assert.True(page.Work.IsIdle);
        Assert.Equal(0, failures());
    }

    /// <summary>A protected path the run took is §5.6 failing, which the taskbar shows as a failure.</summary>
    [Fact]
    public void ACleanWhoseVerificationFailsSaysItFailed()
    {
        var cache = new FakeCleanupProvider("cache");
        using var page = new StoragePage([cache]);
        var tree = page.Cache("tree", 1024);
        var inside = Path.Combine(tree.Path, "entry.bin");
        cache.Steps = [tree];
        cache.ProtectedPaths = [new ProtectedPath(inside, "Must survive.", PathPresence.Present)];
        cache.AfterCleaning = () => (cache.Steps, cache.ProtectedPaths) = ([], []);
        page.Scan();
        var failures = Failures(page.Work);

        page.Clean();

        Assert.True(page.ViewModel.RunVerificationFailed);
        Assert.True(page.Work.IsIdle);
        Assert.Equal(1, failures());
    }

    private static Func<int> Failures(BusyWork work)
    {
        var count = 0;
        work.Failed += (_, _) => count++;

        return () => count;
    }
}
