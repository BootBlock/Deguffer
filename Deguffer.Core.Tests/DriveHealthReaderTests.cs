using Deguffer.Core.Execution;
using Deguffer.Core.Health;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: the two phases, one drive not ending the others, and a drive that does not answer never
/// being asked twice. Driven through the fakes, so no real disk is asked anything.
/// </summary>
public sealed class DriveHealthReaderTests
{
    private readonly FakeDriveProbe _probe = new();
    private readonly FakeVolumeInventory _volumes = new();
    private readonly FakeStorageQueries _storage = new();

    private DriveHealthReader Reader(TimeSpan? limit = null) =>
        new(_probe, _volumes, _storage, limit ?? TimeSpan.FromSeconds(30));

    private sealed class Collected<T> : IProgress<T>
    {
        public System.Collections.Concurrent.ConcurrentBag<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }

    /// <summary>Every disk's extents wait until released, standing for a volume that will not answer.</summary>
    private sealed class StalledStorage(ManualResetEventSlim release) : IStorageQueries
    {
        public StorageAnswer DiskExtents(string mountPoint)
        {
            release.Wait();
            return StorageAnswer.Failed(FakeStorageQueries.ErrorFileNotFound);
        }

        public StorageAnswer Adapter(int disk) => StorageAnswer.Failed(FakeStorageQueries.ErrorFileNotFound);

        public StorageAnswer SeekPenalty(int disk) => StorageAnswer.Failed(FakeStorageQueries.ErrorFileNotFound);

        public StorageAnswer Provisioning(int disk) => StorageAnswer.Failed(FakeStorageQueries.ErrorFileNotFound);
    }

    private readonly Collected<DriveVolumes> _placed = new();

    private async Task<IReadOnlyList<DriveHealth>> ReadAll(DriveHealthReader? reader = null)
    {
        reader ??= Reader();
        var drives = reader.List()!;
        var collected = new Collected<DriveHealth>();

        await reader.ReadAsync(drives, collected, _placed, CancellationToken.None);
        return [.. collected.Reports.OrderBy(report => report.Name, StringComparer.Ordinal)];
    }

    private FakeDriveProbe Nvme(int disk, byte[]? health = null, string model = "Test NVMe") =>
        _probe
            .Answer(disk, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, model, serial: $"NVME{disk}"))
            .Answer(disk, DriveQuestion.NvmeHealth, health ?? DriveBytes.NvmeHealth());

    private FakeDriveProbe Sata(int disk, string model = "Test SATA", params AtaEntry[] entries) =>
        _probe
            .Answer(disk, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Sata, model, serial: $"SATA{disk}"))
            .Answer(disk, DriveQuestion.PredictFailure, DriveBytes.Prediction(false, entries));

    [Fact]
    public void ListingOrdersDrivesByDiskAndNamesThem()
    {
        Nvme(2, model: "Second");
        Sata(0, "First");

        var drives = Reader().List()!;

        Assert.Equal(new[] { 0, 2 }, drives.Select(drive => drive.Disk));
        Assert.Equal("First", drives[0].Name);
    }

    [Fact]
    public void ListingAsksOnlyForTheDeviceDescriptor()
    {
        Nvme(0);

        Reader().List();

        Assert.All(_probe.Asked, asked => Assert.Equal(DriveQuestion.Device, asked.Question));
    }

    [Fact]
    public void ListingIsNullWhereWindowsWillNotList()
    {
        _probe.ListFails = true;

        Assert.Null(Reader().List());
    }

    [Fact]
    public void ADriveThatWillNotDescribeItselfStillHasARow()
    {
        _probe.Refuse(3, DriveQuestion.Device, FakeDriveProbe.ErrorInvalidFunction);

        Assert.Equal("Disk 3", Assert.Single(Reader().List()!).Name);
    }

    [Fact]
    public async Task AnNvmeDriveGivesAGoodVerdictWhenItsLogIsClean()
    {
        Nvme(0, DriveBytes.NvmeHealth(kelvin: 310));

        var report = Assert.Single(await ReadAll());

        Assert.Equal(DriveVerdict.Good, report.Verdict);
        Assert.Equal("37 °C", report.Temperature);
        Assert.False(report.MoreWhenElevated);
    }

    [Fact]
    public async Task AUsbDriveIsListedAndAskedNoHealthQuestion()
    {
        _probe.Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Usb, "Stick", removable: true));

        var report = Assert.Single(await ReadAll());

        Assert.Equal(DriveVerdict.Unknown, report.Verdict);
        Assert.Contains("does not report", report.Summary, StringComparison.Ordinal);
        Assert.Contains(report.Facts, fact => fact.Value == "USB (removable)");
        Assert.DoesNotContain(_probe.Asked, asked => asked.Question is DriveQuestion.NvmeHealth or DriveQuestion.PredictFailure or DriveQuestion.AtaThresholds);
    }

    [Fact]
    public async Task ASataDriveRefusedItsCommandsStillGivesItsAttributesAndOffersElevation()
    {
        Sata(0, "Test SATA", new AtaEntry(5, 100));
        _probe.Refuse(0, DriveQuestion.AtaThresholds, FakeDriveProbe.ErrorAccessDenied);

        var report = Assert.Single(await ReadAll());

        Assert.Equal(DriveVerdict.Good, report.Verdict);
        Assert.True(report.MoreWhenElevated);
        Assert.Contains(report.Attributes, attribute => attribute.Name == "Reallocated sectors");
        Assert.DoesNotContain("threshold", report.Attributes.First(attribute => attribute.Name == "Reallocated sectors").Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatFailsForAnotherReasonOffersNoElevation()
    {
        Sata(0);
        _probe.Refuse(0, DriveQuestion.AtaThresholds, FakeDriveProbe.ErrorInvalidFunction);

        Assert.False(Assert.Single(await ReadAll()).MoreWhenElevated);
    }

    [Fact]
    public async Task ElevatedThresholdsAreCompared()
    {
        Sata(0, "Test SATA", new AtaEntry(5, 30, PreFail: true));
        _probe.Answer(0, DriveQuestion.AtaThresholds, DriveBytes.Ata(DriveBytes.Thresholds((5, 36))));

        Assert.Equal(DriveVerdict.Failing, Assert.Single(await ReadAll()).Verdict);
    }

    [Fact]
    public async Task ElevatedIdentifyGivesRotationAndLink()
    {
        Sata(0);
        _probe.Answer(0, DriveQuestion.AtaIdentify, DriveBytes.Ata(DriveBytes.Identify(7200, 0b1110, 3)));

        var report = Assert.Single(await ReadAll());

        Assert.Contains(report.Facts, fact => fact is { Label: "Rotation speed", Value: "7,200 RPM" });
        Assert.Contains(report.Facts, fact => fact is { Label: "SATA link speed", Value: "6 Gb/s now, up to 6 Gb/s" });
    }

    [Fact]
    public async Task ADriveRejectingEveryQuestionIsUnknownAndDoesNotEndTheOthers()
    {
        _probe.Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Silent", serial: "S0"));
        Nvme(1, model: "Working");

        var reports = await ReadAll();

        Assert.Equal(DriveVerdict.Unknown, reports.Single(report => report.Name == "Silent").Verdict);
        Assert.Equal(DriveVerdict.Good, reports.Single(report => report.Name == "Working").Verdict);
    }

    [Fact]
    public async Task ADriveGoneMidReadStillYieldsItsRow()
    {
        Nvme(0);
        var reader = Reader();
        var drives = reader.List()!;
        var collected = new Collected<DriveHealth>();

        _probe.Remove(0);
        await reader.ReadAsync(drives, collected, _placed, CancellationToken.None);

        var report = Assert.Single(collected.Reports);
        Assert.Equal(DriveVerdict.Unknown, report.Verdict);
        Assert.Empty(report.Attributes);
    }

    [Fact]
    public async Task TwoDrivesReportingTheSameSerialAreEachRead()
    {
        _probe
            .Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Twin", serial: "SAME"))
            .Answer(0, DriveQuestion.NvmeHealth, DriveBytes.NvmeHealth(kelvin: 310))
            .Answer(1, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Twin", serial: "SAME"))
            .Answer(1, DriveQuestion.NvmeHealth, DriveBytes.NvmeHealth(kelvin: 320));

        var reports = await ReadAll();

        Assert.Equal(2, reports.Select(report => report.Key).Distinct().Count());
        Assert.Equivalent(new[] { "37 °C", "47 °C" }, reports.Select(report => report.Temperature));
    }

    [Fact]
    public async Task EachDetailIsReadFromTheDrive()
    {
        Nvme(0)
            .Answer(0, DriveQuestion.SeekPenalty, DriveBytes.SeekPenalty(false))
            .Answer(0, DriveQuestion.Trim, DriveBytes.Trim(true))
            .Answer(0, DriveQuestion.Alignment, DriveBytes.Alignment(512, 4096))
            .Answer(0, DriveQuestion.Geometry, DriveBytes.Geometry(2L << 30));

        var facts = Assert.Single(await ReadAll()).Facts;

        Assert.Contains(facts, fact => fact is { Label: "Type", Value: "Solid state" });
        Assert.Contains(facts, fact => fact is { Label: "TRIM", Value: "On" });
        Assert.Contains(facts, fact => fact is { Label: "Sector size", Value: "512 bytes, 4096 bytes physical" });
        Assert.Contains(facts, fact => fact is { Label: "Capacity", Value: "2.0 GB" });
    }

    [Fact]
    public async Task AWrittenCountInLogicalSectorsIsSizedByTheSectorSizeTheDriveReports()
    {
        _probe
            .Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Sata, "Crucial_CT999MX200SSD1", serial: "SATA0", revision: "MU01"))
            .Answer(0, DriveQuestion.PredictFailure, DriveBytes.Prediction(false, new AtaEntry(246, 100, Raw: 2048)))
            .Answer(0, DriveQuestion.Alignment, DriveBytes.Alignment(4096, 4096));

        var report = Assert.Single(await ReadAll());

        Assert.Contains(report.Attributes, attribute => attribute is { Name: "Total data written", Reading: "8.0 MB" });
        Assert.Single(_probe.Asked, asked => asked.Question == DriveQuestion.Alignment);
    }

    [Fact]
    public async Task ALinkSpeedTheDriveDoesNotLimitSaysOnlyWhatItKnows()
    {
        Sata(0);
        _probe.Answer(0, DriveQuestion.AtaIdentify, DriveBytes.Ata(DriveBytes.Identify(1, 0, 2)));

        Assert.Contains(Assert.Single(await ReadAll()).Facts, fact => fact is { Label: "SATA link speed", Value: "3 Gb/s now" });
    }

    [Fact]
    public async Task AnNvmeDriveRefusedItsLogOffersElevation()
    {
        _probe
            .Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Refused", serial: "S0"))
            .Refuse(0, DriveQuestion.NvmeHealth, FakeDriveProbe.ErrorAccessDenied);

        Assert.True(Assert.Single(await ReadAll()).MoreWhenElevated);
    }

    [Fact]
    public async Task AVolumeThatWillNotAnswerCostsOnlyItsLine()
    {
        Nvme(0);
        _volumes.With(@"C:\");
        using var release = new ManualResetEventSlim();
        var reader = new DriveHealthReader(_probe, _volumes, new StalledStorage(release), TimeSpan.FromMilliseconds(50));

        var report = Assert.Single(await ReadAll(reader));

        Assert.Equal(DriveVerdict.Good, report.Verdict);
        Assert.Empty(_placed.Reports);

        release.Set();
    }

    [Fact]
    public async Task ADriveReportsWhileAVolumeIsStillStalled()
    {
        Nvme(0);
        _volumes.With(@"C:\");
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var collected = new Collected<DriveHealth>();
        var reader = new DriveHealthReader(_probe, _volumes, new StalledStorage(release), TimeSpan.FromMinutes(5));

        var reading = reader.ReadAsync(reader.List()!, collected, _placed, stop.Token);
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (collected.Reports.IsEmpty && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }

        var arrived = !collected.Reports.IsEmpty;

        await stop.CancelAsync();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        Assert.True(arrived, "The drive's reading waited for a volume that had not answered.");
    }

    [Fact]
    public async Task AListingThatDoesNotAnswerIsNoListing()
    {
        using var release = new ManualResetEventSlim();

        _probe.Answer(0, DriveQuestion.Device, () =>
        {
            release.Wait();
            return StorageAnswer.Failed(FakeDriveProbe.ErrorInvalidFunction);
        });

        Assert.Null(await Reader(TimeSpan.FromMilliseconds(50)).ListAsync(CancellationToken.None));

        release.Set();
    }

    [Fact]
    public void ADriveWithNoReportKeepsTheHeadlineFromReassuring()
    {
        var unknown = Report(moreWhenElevated: false) with { Verdict = DriveVerdict.Unknown };

        Assert.Equal("2 drives, 1 with no health report.", DriveOverview.Headline(2, [Report(moreWhenElevated: false), unknown]));
    }

    [Theory]
    [InlineData(0, 0, 0, "No drives found.")]
    [InlineData(1, 1, 0, "1 drive, no concerns reported.")]
    [InlineData(3, 3, 0, "3 drives, no concerns reported.")]
    [InlineData(3, 1, 0, "Reading 3 drives…")]
    [InlineData(2, 2, 1, "2 drives, 1 needs attention.")]
    [InlineData(4, 4, 2, "4 drives, 2 need attention.")]
    [InlineData(3, 1, 1, "3 drives, 1 needs attention.")]
    public void TheHeadlineReassuresOnlyOnceEveryDriveHasAnswered(int drives, int read, int concerns, string expected)
    {
        var reports = Enumerable.Range(0, read)
            .Select(index => Report(moreWhenElevated: false) with { Verdict = index < concerns ? DriveVerdict.Watch : DriveVerdict.Good })
            .ToList();

        Assert.Equal(expected, DriveOverview.Headline(drives, reports));
    }

    [Fact]
    public async Task VolumesAreListedAgainstTheirDisk()
    {
        Nvme(4);
        _volumes.With(@"C:\", totalBytes: 2L << 30, freeBytes: 1L << 30);
        _storage.Volume(@"C:\", 4).Disk(4, DriveBytes.Nvme, seekPenalty: false);

        var report = Assert.Single(await ReadAll());

        Assert.Equal(report.Key, Assert.Single(_placed.Reports).Key);
        Assert.Contains(@"C:\", _placed.Reports.Single().Line, StringComparison.Ordinal);
        Assert.Equal([@"C:\"], _placed.Reports.Single().RootPaths);
    }

    [Fact]
    public async Task AVolumeIsPlacedByEveryPlaceItIsMounted()
    {
        Nvme(4);
        _volumes.With(@"D:\", alsoMountedAt: [@"C:\Data\"]);
        _storage.Volume(@"D:\", 4).Disk(4, DriveBytes.Nvme, seekPenalty: false);

        await ReadAll();

        Assert.Equal([@"D:\", @"C:\Data\"], Assert.Single(_placed.Reports).RootPaths);
    }

    [Fact]
    public async Task ADriveThatDoesNotAnswerIsReportedAndNotAskedAgain()
    {
        using var release = new ManualResetEventSlim();
        var asked = 0;

        _probe
            .Answer(0, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Stalled", serial: "S0"))
            .Answer(0, DriveQuestion.NvmeHealth, () =>
            {
                Interlocked.Increment(ref asked);
                release.Wait();
                return StorageAnswer.Answered(DriveBytes.NvmeHealth());
            });

        var reader = Reader(TimeSpan.FromMilliseconds(50));

        var first = Assert.Single(await ReadAll(reader));
        Assert.Contains("did not answer", first.Summary, StringComparison.Ordinal);

        var second = Assert.Single(await ReadAll(reader));
        Assert.Contains("did not answer", second.Summary, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref asked));

        release.Set();
    }

    [Fact]
    public async Task CancellingStopsFurtherDrivesBeingRead()
    {
        const int Drives = 12;
        const int MostAtOnce = 4;
        using var cancelled = new CancellationTokenSource();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;

        for (var disk = 0; disk < Drives; disk++)
        {
            _probe
                .Answer(disk, DriveQuestion.Device, DriveBytes.Device(DriveBytes.Nvme, "Drive", serial: $"S{disk}"))
                .Answer(disk, DriveQuestion.NvmeHealth, () =>
                {
                    Interlocked.Increment(ref asked);
                    held.Task.Wait();
                    return StorageAnswer.Answered(DriveBytes.NvmeHealth());
                });
        }

        var reader = Reader();
        var reading = reader.ReadAsync(reader.List()!, new Collected<DriveHealth>(), _placed, cancelled.Token);
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (Volatile.Read(ref asked) == 0 && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(5);
        }

        // Every drive reached is held here, so nothing can move on to the next until after the cancel.
        await cancelled.CancelAsync();
        held.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        Assert.InRange(Volatile.Read(ref asked), 1, MostAtOnce);
    }

    [Fact]
    public void TheOfferFollowsWhetherElevationWouldShowMore()
    {
        var refused = Report(moreWhenElevated: true);
        var answered = Report(moreWhenElevated: false);

        Assert.True(ElevationOffer.ShouldOffer(false, [answered, refused]));
        Assert.False(ElevationOffer.ShouldOffer(true, [refused]));
        Assert.False(ElevationOffer.ShouldOffer(false, [answered]));
    }

    private static DriveHealth Report(bool moreWhenElevated) =>
        new(new DriveKey("k"), "n", DriveVerdict.Good, "s", null, [], [], [], moreWhenElevated);
}
