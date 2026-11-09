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

    private sealed class Collected : IProgress<DriveHealth>
    {
        public System.Collections.Concurrent.ConcurrentBag<DriveHealth> Reports { get; } = [];

        public void Report(DriveHealth value) => Reports.Add(value);
    }

    private async Task<IReadOnlyList<DriveHealth>> ReadAll(DriveHealthReader? reader = null)
    {
        reader ??= Reader();
        var drives = reader.List()!;
        var collected = new Collected();

        await reader.ReadAsync(drives, collected, CancellationToken.None);
        return [.. collected.Reports.OrderBy(report => report.Disk)];
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
    public void ListingAsksOnlyWhatTheDriverAnswersFromMemory()
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
        Assert.Equal(37, report.TemperatureCelsius);
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
        Nvme(1);

        var reports = await ReadAll();

        Assert.Equal(DriveVerdict.Unknown, reports[0].Verdict);
        Assert.Equal(DriveVerdict.Good, reports[1].Verdict);
    }

    [Fact]
    public async Task ADriveGoneMidReadStillYieldsItsRow()
    {
        Nvme(0);
        var reader = Reader();
        var drives = reader.List()!;
        var collected = new Collected();

        _probe.Remove(0);
        await reader.ReadAsync(drives, collected, CancellationToken.None);

        Assert.Single(collected.Reports);
    }

    [Fact]
    public async Task VolumesAreListedAgainstTheirDisk()
    {
        Nvme(4);
        _volumes.With(@"C:\", totalBytes: 2L << 30, freeBytes: 1L << 30);
        _storage.Volume(@"C:\", 4).Disk(4, DriveBytes.Nvme, seekPenalty: false);

        var report = Assert.Single(await ReadAll());

        Assert.Contains(@"C:\", report.Volumes, StringComparison.Ordinal);
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
        Nvme(0);
        var reader = Reader();
        var drives = reader.List()!;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(drives, new Collected(), cancelled.Token));
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
        new(new DriveKey("k"), 0, "n", DriveVerdict.Good, "s", null, [], [], [], moreWhenElevated, null);
}
