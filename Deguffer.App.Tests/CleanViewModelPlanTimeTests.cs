using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Storage page wires each provider's planning time (#443): a full scan adds to it, and a scan
/// of the chosen locations or one that failed does not. Which passes count is Core's, and tested there.
/// </summary>
public sealed class CleanViewModelPlanTimeTests
{
    [Fact]
    public void AFullScanKeepsEachProvidersPlanningTime()
    {
        FakeCleanupProvider npm = new("npm"), conda = new("conda");
        using var page = new StoragePage([npm, conda]);

        page.Scan();

        Assert.NotNull(page.PlanTimes.MedianOf("npm", elevated: false));
        Assert.NotNull(page.PlanTimes.MedianOf("conda", elevated: false));
        Assert.Null(page.PlanTimes.MedianOf("npm", elevated: true));
    }

    /// <summary>A scan of some locations planned beside fewer rows than a full one, so nothing it timed is kept.</summary>
    [Fact]
    public void AChosenScanKeepsNoPlanningTime()
    {
        FakeCleanupProvider npm = new("npm"), conda = new("conda");
        using var page = new StoragePage([npm, conda]);

        page.ViewModel.ScanChoice.Choose([npm], chosen: true);
        page.ScanChosen();

        Assert.Single(page.ViewModel.Findings);
        Assert.Null(page.PlanTimes.MedianOf("npm", elevated: false));
    }

    /// <summary>A scan that failed ended its rows early, so nothing it timed is kept.</summary>
    [Fact]
    public void AFailedScanKeepsNoPlanningTime()
    {
        FakeCleanupProvider npm = new("npm"), broken = new("broken") { PlanFailure = new IOException("The device is not ready.") };
        using var page = new StoragePage([npm, broken]);

        page.Scan();

        Assert.Null(page.PlanTimes.MedianOf("npm", elevated: false));
    }
}
