using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// DISM's component store analysis, read from its English report. The sample is the report as
/// Windows 11 24H2 wrote it, progress bars included, because they arrive on the same stream.
/// </summary>
public sealed class ComponentStoreReportTests
{
    public const string Sample =
        "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.1\r\n\r\n"
        + "Image Version: 10.0.26100.1\r\n\r\n\r\n"
        + "[==                         4.1%                           ] \r\n\r\n"
        + "[==========================100.0%==========================] \r\n\r\n"
        + "Component Store (WinSxS) information:\r\n\r\n"
        + "Windows Explorer Reported Size of Component Store : 23.36 GB\r\n\r\n"
        + "Actual Size of Component Store : 21.33 GB\r\n\r\n"
        + "    Shared with Windows : 8.35 GB\r\n"
        + "    Backups and Disabled Features : 12.98 GB\r\n"
        + "    Cache and Temporary Data :  0 bytes\r\n\r\n"
        + "Date of Last Cleanup : 2026-09-25 06:30:36\r\n\r\n"
        + "Number of Reclaimable Packages : 7\r\n"
        + "Component Store Cleanup Recommended : Yes\r\n\r\n"
        + "The operation completed successfully.\r\n";

    [Fact]
    public void EveryFigureIsReadInDecimalUnits()
    {
        var report = ComponentStoreReport.Parse(Sample);

        Assert.NotNull(report);
        Assert.Equal(23_360_000_000, report.ExplorerSize);
        Assert.Equal(21_330_000_000, report.ActualSize);
        Assert.Equal(8_350_000_000, report.SharedWithWindows);
        Assert.Equal(12_980_000_000, report.BackupsAndDisabledFeatures);
        Assert.Equal(0, report.CacheAndTemporaryData);
        Assert.Equal("2026-09-25 06:30:36", report.LastCleanup);
        Assert.Equal(7, report.ReclaimablePackages);
        Assert.True(report.CleanupRecommended);
        Assert.Equal(12_980_000_000, report.Overhead);
    }

    [Theory]
    [InlineData("0 bytes", 0L)]
    [InlineData("512 bytes", 512L)]
    [InlineData("369.91 MB", 369_910_000L)]
    [InlineData("369,91 MB", 369_910_000L)]
    [InlineData("1,000.5 MB", 1_000_500_000L)]
    [InlineData("1.000,50 MB", 1_000_500_000L)]
    [InlineData("1,000 MB", 1_000_000_000L)]
    [InlineData("2.5 KB", 2_500L)]
    [InlineData("1.02 TB", 1_020_000_000_000L)]
    public void ASizeIsReadWhicheverSeparatorsTheDisplayLanguageGaveIt(string text, long bytes) =>
        Assert.Equal(bytes, ComponentStoreReport.ParseSize(text));

    [Theory]
    [InlineData("12.98")]
    [InlineData("12.98 GiB")]
    [InlineData("twelve GB")]
    [InlineData("")]
    public void AnythingElseIsNotASize(string text) => Assert.Null(ComponentStoreReport.ParseSize(text));

    /// <summary>Half a report is not a report: a missing actual size would read as an empty store.</summary>
    [Theory]
    [InlineData("Actual Size of Component Store : 21.33 GB")]
    [InlineData("Number of Reclaimable Packages : 7")]
    [InlineData("Component Store Cleanup Recommended : Yes")]
    [InlineData("    Backups and Disabled Features : 12.98 GB")]
    public void AReportMissingAFigureIsNoReport(string line) =>
        Assert.Null(ComponentStoreReport.Parse(Sample.Replace(line, string.Empty, StringComparison.Ordinal)));

    [Fact]
    public void AReportWithNoDateOfLastCleanupStillReads()
    {
        var report = ComponentStoreReport.Parse(
            Sample.Replace("Date of Last Cleanup : 2026-09-25 06:30:36", string.Empty, StringComparison.Ordinal));

        Assert.NotNull(report);
        Assert.Null(report.LastCleanup);
    }

    [Fact]
    public void WindowsVerdictIsRead() =>
        Assert.False(ComponentStoreReport.Parse(
            Sample.Replace("Recommended : Yes", "Recommended : No", StringComparison.Ordinal))!.CleanupRecommended);
}
