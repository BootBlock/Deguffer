using Deguffer.Core.Health;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's line about the drive its free space is on (§7.6): closed until Health has found
/// a concern with a drive under the profile's volume, and following Health's latest reading after
/// that. What the line says is proved in Core.
/// </summary>
public sealed class StorageDriveHealthTests
{
    private static readonly DriveDescription System = new(new DriveKey("a"), 0, "System SSD");

    private static DriveHealth Report(DriveVerdict verdict) => new(System.Key, System.Name, verdict, "summary", null, [], [], [], false);

    [Fact]
    public void TheLineIsClosedUntilHealthFindsAConcern()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")]);

        Assert.False(page.ViewModel.DriveHealth.HasConcern);

        page.DriveHealth.Record(new DriveVolumes(System.Key, "profile", [page.VolumeRoot]));
        page.DriveHealth.Record(Report(DriveVerdict.Good));

        Assert.False(page.ViewModel.DriveHealth.HasConcern);
    }

    [Fact]
    public void AFailingDriveUnderTheProfileOpensTheLineAsAnError()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")]);
        var line = page.ViewModel.DriveHealth;

        page.DriveHealth.Record(new DriveVolumes(System.Key, "profile", [page.VolumeRoot]));
        page.DriveHealth.Record(Report(DriveVerdict.Failing));

        Assert.True(line.HasConcern);
        Assert.Equal("Drive health: Failing", line.Title);
        Assert.StartsWith("System SSD reports a serious problem.", line.Text, StringComparison.Ordinal);
        Assert.Equal(InfoBarSeverity.Error, line.Severity);

        page.DriveHealth.Record(Report(DriveVerdict.Watch));

        Assert.Equal("Drive health: Watch", line.Title);
        Assert.Equal(InfoBarSeverity.Warning, line.Severity);

        page.DriveHealth.Record(Report(DriveVerdict.Good));

        Assert.False(line.HasConcern);
        Assert.Equal(string.Empty, line.Text);
    }

    [Fact]
    public void AConcernWithAnotherVolumesDriveLeavesTheLineClosed()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")]);

        page.DriveHealth.Record(new DriveVolumes(System.Key, "elsewhere", [@"Q:\"]));
        page.DriveHealth.Record(Report(DriveVerdict.Failing));

        Assert.False(page.ViewModel.DriveHealth.HasConcern);
    }
}
