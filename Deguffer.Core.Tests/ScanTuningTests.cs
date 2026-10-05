using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a scan runs with: Auto for each kind of drive, the user's number where they chose one, and
/// both read when the scan starts rather than when the scanner was built.
/// </summary>
public sealed class ScanTuningTests
{
    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    /// <summary>
    /// A drive of unknown kind is read as every drive was before the values could be set, but for
    /// the parse threads, which belong to the machine and were measured on it.
    /// </summary>
    [Fact]
    public void AutoIsWhatScansUsedBeforeTheValuesCouldBeSetOnADriveOfUnknownKind()
    {
        var tuning = VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Unknown);

        Assert.Equal(WalkTuning.Default, tuning.Walk);
        Assert.Equal(TableTuning.Default.ReadBytes, tuning.Table.ReadBytes);
        Assert.Equal(TableTuning.Default.ReadsInFlight, tuning.Table.ReadsInFlight);
        Assert.Equal(Math.Min(Environment.ProcessorCount, 4), tuning.Table.ParseThreads);
        Assert.Equal(MediaScanPreferences.Auto, tuning.Chosen);
    }

    /// <summary>
    /// The values measured to differ by kind: NVMe reads the table fastest in reads of 1 MiB with
    /// eight in flight, other solid state in the largest reads one at a time, and a kind not measured
    /// keeps what every scan used before.
    /// </summary>
    [Theory]
    [InlineData(StorageMedia.Nvme, 1024 * 1024, 8)]
    [InlineData(StorageMedia.SolidState, TableTuning.MaximumReadBytes, 1)]
    [InlineData(StorageMedia.Rotational, 1024 * 1024, 1)]
    [InlineData(StorageMedia.Removable, 1024 * 1024, 1)]
    [InlineData(StorageMedia.Network, 1024 * 1024, 1)]
    [InlineData(StorageMedia.Virtual, 1024 * 1024, 1)]
    public void AutoReadsTheTableAsMeasuredForTheKindOfDrive(StorageMedia media, int readBytes, int readsInFlight)
    {
        var tuning = VolumeTuning.Resolve(ScanPreferences.Default, media);

        Assert.Equal(readBytes, tuning.Table.ReadBytes);
        Assert.Equal(readsInFlight, tuning.Table.ReadsInFlight);
        Assert.Equal(WalkTuning.Default, tuning.Walk);
    }

    [Fact]
    public void AChosenValueIsUsedForItsOwnKindOfDriveAndNoOther()
    {
        var preferences = ScanPreferences.Default.With(
            StorageMedia.Rotational,
            new MediaScanPreferences(WalkThreads: 2, ListingBufferKiB: 64, TableReadKiB: 256, TableReadsInFlight: 5));

        var spinning = VolumeTuning.Resolve(preferences, StorageMedia.Rotational);
        var nvme = VolumeTuning.Resolve(preferences, StorageMedia.Nvme);

        Assert.Equal(new WalkTuning(2, 64 * 1024), spinning.Walk);
        Assert.Equal(256 * 1024, spinning.Table.ReadBytes);
        Assert.Equal(5, spinning.Table.ReadsInFlight);
        Assert.Equal(VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Nvme), nvme);
    }

    /// <summary>Parsing is the processor's work, so one number of threads reads every kind of drive.</summary>
    [Fact]
    public void TheParseThreadsAreOneValueForEveryKindOfDrive()
    {
        var preferences = ScanPreferences.Default with { TableParseThreads = 7 };

        foreach (var media in Enum.GetValues<StorageMedia>())
        {
            Assert.Equal(7, VolumeTuning.Resolve(preferences, media).Table.ParseThreads);
        }
    }

    /// <summary>
    /// Nothing validates <c>preferences.json</c> on the way in, so a hand-edited number outside its
    /// bounds reaches the reader, and must not throw out of a scan. A size near the largest integer
    /// is clamped before it becomes bytes, or it would overflow to a negative size.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-5, -5, -5)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void AStoredValueOutsideItsBoundsIsClampedAsItIsRead(int threads, int listingKiB, int tableKiB)
    {
        var preferences = ScanPreferences.Default with
        {
            Unknown = new MediaScanPreferences(threads, listingKiB, tableKiB, TableReadsInFlight: threads),
            TableParseThreads = threads,
        };

        var tuning = VolumeTuning.Resolve(preferences, StorageMedia.Unknown);

        Assert.Equal(threads > 0 ? WalkTuning.MaximumThreads : WalkTuning.MinimumThreads, tuning.Walk.Threads);
        Assert.Equal(
            listingKiB > 0 ? WalkTuning.MaximumListingBuffer : WalkTuning.MinimumListingBuffer,
            tuning.Walk.ListingBufferBytes);
        Assert.Equal(
            tableKiB > 0 ? TableTuning.MaximumReadBytes : TableTuning.MinimumReadBytes,
            tuning.Table.ReadBytes);
        Assert.Equal(
            threads > 0 ? TableTuning.MaximumReadsInFlight : TableTuning.MinimumReadsInFlight,
            tuning.Table.ReadsInFlight);
        Assert.Equal(
            threads > 0 ? TableTuning.MaximumParseThreads : TableTuning.MinimumParseThreads,
            tuning.Table.ParseThreads);
    }

    /// <summary>Every kind has its own values, and setting one leaves every other kind as it was.</summary>
    [Fact]
    public void EachKindOfDriveHoldsItsOwnValues()
    {
        var preferences = ScanPreferences.Default;

        foreach (var (media, i) in Enum.GetValues<StorageMedia>().Select((media, i) => (media, i)))
        {
            preferences = preferences.With(media, new MediaScanPreferences(WalkThreads: i + 1));
        }

        foreach (var (media, i) in Enum.GetValues<StorageMedia>().Select((media, i) => (media, i)))
        {
            Assert.Equal(i + 1, preferences.For(media).WalkThreads);
        }
    }

    [Fact]
    public void AShareIsANetworkDriveWithoutAskingAnything()
    {
        var queries = new FakeStorageQueries();
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), queries, new FakeVolumeInventory());

        Assert.Equal(StorageMedia.Network, tuner.MediaOf(@"\\server.test\share\cache"));
        Assert.Equal(StorageMedia.Network, tuner.MediaOf(@"\\?\UNC\server.test\share\cache"));
        Assert.Equal(0, queries.ExtentsAsked);
    }

    [Fact]
    public void APathIsTunedForTheKindOfDriveThatHoldsIt()
    {
        var preferences = new FakePreferences(AppPreferences.Default with
        {
            Scanning = ScanPreferences.Default.With(StorageMedia.Nvme, new MediaScanPreferences(WalkThreads: 3)),
        });
        var tuner = Tuner(
            preferences,
            new FakeStorageQueries().Volume(@"D:\", 1).Disk(1, Nvme, seekPenalty: false),
            new FakeVolumeInventory().With(@"D:\"));

        var tuning = tuner.For(@"\\?\D:\Users\testuser\.npm-cache");

        Assert.Equal(StorageMedia.Nvme, tuning.Media);
        Assert.Equal(3, tuning.Walk.Threads);
    }

    /// <summary>
    /// A path on no volume the inventory lists gets the conservative values. A volume named by its
    /// GUID starts with two separators as a share does, and is a local volume of unknown kind rather
    /// than a share.
    /// </summary>
    [Theory]
    [InlineData(@"Q:\cache")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\cache")]
    [InlineData(@"\\.\Volume{00000000-0000-0000-0000-000000000000}\cache")]
    public void APathOnNoKnownVolumeIsOfUnknownKind(string path)
    {
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), new FakeStorageQueries(), new FakeVolumeInventory());

        Assert.Equal(StorageMedia.Unknown, tuner.For(path).Media);
    }

    /// <summary>
    /// The scanners are built once for the session, and the user changes a setting at any time. A
    /// value captured when the tuner was built would go stale silently.
    /// </summary>
    [Fact]
    public void TheSettingsAreReadWhenAScanStartsRatherThanWhenTheTunerWasBuilt()
    {
        var preferences = new FakePreferences(AppPreferences.Default);
        var tuner = Tuner(preferences, new FakeStorageQueries(), new FakeVolumeInventory());

        Assert.False(tuner.WalkOnly);
        Assert.Equal(WalkTuning.Default.Threads, tuner.For(@"Q:\cache").Walk.Threads);

        preferences.Current = AppPreferences.Default with
        {
            Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: 5)) with
            {
                Route = ScanRoute.WalkOnly,
            },
        };

        Assert.True(tuner.WalkOnly);
        Assert.Equal(5, tuner.For(@"Q:\cache").Walk.Threads);
    }

    /// <summary>
    /// The kind of each disk is asked once, however many scans start, until the tuner is told to
    /// forget, which a planning pass does at its start.
    /// </summary>
    [Fact]
    public void ADrivesKindIsAskedOnceUntilForgotten()
    {
        var queries = new FakeStorageQueries().Volume(@"D:\", 1).Disk(1, Sata, seekPenalty: false);
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), queries, new FakeVolumeInventory().With(@"D:\"));

        tuner.For(@"D:\a");
        tuner.For(@"D:\b");
        Assert.Equal(1, queries.ExtentsAsked);

        tuner.Invalidate();
        tuner.For(@"D:\a");
        Assert.Equal(2, queries.ExtentsAsked);
    }

    [Fact]
    public void TheShippedTunerAsksNoDeviceAndUsesTheShippedValues()
    {
        Assert.Equal(StorageMedia.Unknown, ScanTuner.Shipped.MediaOf(@"C:\Windows"));
        Assert.False(ScanTuner.Shipped.WalkOnly);
        Assert.Empty(ScanTuner.Shipped.Drives(CancellationToken.None));
    }

    /// <summary>
    /// What the Settings page lists: each ready drive with a letter, in order, and nothing for a
    /// drive with no media or a volume mounted only at a folder.
    /// </summary>
    [Fact]
    public void TheDrivesListedAreTheReadyOnesWithALetterInOrder()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"C:\", 1).Disk(1, Nvme, seekPenalty: false)
            .Volume(@"D:\", 0).Disk(0, Sata, seekPenalty: true);
        var volumes = new FakeVolumeInventory()
            .With(@"D:\")
            .With(@"C:\")
            .With(@"E:\", DriveType.Removable, VolumeReadiness.NoMedia)
            .With(@"C:\Mount\");
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), queries, volumes);

        Assert.Equal(
            [new DriveKind("C:", StorageMedia.Nvme), new DriveKind("D:", StorageMedia.Rotational)],
            tuner.Drives(CancellationToken.None));
    }

    /// <summary>Each drive's first answer waits on its device, so a listing nobody wants any more stops.</summary>
    [Fact]
    public void AListingOfDrivesStopsWhenCancelled()
    {
        var queries = new FakeStorageQueries().Volume(@"C:\", 1).Disk(1, Nvme, seekPenalty: false);
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), queries, new FakeVolumeInventory().With(@"C:\"));

        Assert.Throws<OperationCanceledException>(() => tuner.Drives(new CancellationToken(canceled: true)));
        Assert.Equal(0, queries.ExtentsAsked);
    }

    [Fact]
    public void ADrivesLineSaysWhichValuesAutoChose()
    {
        var preferences = ScanPreferences.Default.With(StorageMedia.Nvme, new MediaScanPreferences(TableReadKiB: 4096)) with
        {
            TableParseThreads = 3,
        };

        var line = ScanTuningText.Describe(new DriveKind("C:", StorageMedia.Nvme), preferences);
        var auto = VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Nvme);

        Assert.StartsWith("C: (NVMe SSD): ", line, StringComparison.Ordinal);
        Assert.Contains($"{auto.Walk.Threads} threads (Auto)", line, StringComparison.Ordinal);
        Assert.Contains($"{auto.Walk.ListingBufferBytes / 1024} KiB listing buffer (Auto)", line, StringComparison.Ordinal);
        Assert.Contains($"{4096:N0} KiB table reads", line, StringComparison.Ordinal);
        Assert.DoesNotContain("table reads (Auto)", line, StringComparison.Ordinal);
        Assert.Contains($"{auto.Table.ReadsInFlight} in flight (Auto)", line, StringComparison.Ordinal);
        Assert.EndsWith("parsed on 3 threads", line, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKindOfDriveHasAName()
    {
        foreach (var media in Enum.GetValues<StorageMedia>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ScanTuningText.MediaName(media)));
        }
    }

    /// <summary>
    /// An emptied box reports <see cref="double.NaN"/>, and for a scan value that is the way back to
    /// Auto. A number is rounded and held to the bounds the reader holds it to.
    /// </summary>
    [Fact]
    public void ANumberTypedForAScanValueIsStoredWithinItsBoundsAndAnEmptyBoxIsAuto()
    {
        Assert.Null(EnteredSetting.WalkThreads(double.NaN));
        Assert.Null(EnteredSetting.ListingBufferKiB(double.NaN));
        Assert.Null(EnteredSetting.TableReadKiB(double.NaN));
        Assert.Null(EnteredSetting.TableReadsInFlight(double.NaN));
        Assert.Null(EnteredSetting.TableParseThreads(double.NaN));

        Assert.Equal(8, EnteredSetting.WalkThreads(7.5));
        Assert.Equal(WalkTuning.MinimumThreads, EnteredSetting.WalkThreads(0));
        Assert.Equal(WalkTuning.MaximumThreads, EnteredSetting.WalkThreads(1000));
        Assert.Equal(WalkTuning.MinimumListingBuffer / 1024, EnteredSetting.ListingBufferKiB(1));
        Assert.Equal(WalkTuning.MaximumListingBuffer / 1024, EnteredSetting.ListingBufferKiB(double.PositiveInfinity));
        Assert.Equal(TableTuning.MinimumReadBytes / 1024, EnteredSetting.TableReadKiB(double.NegativeInfinity));
        Assert.Equal(TableTuning.MaximumReadBytes / 1024, EnteredSetting.TableReadKiB(1e9));
        Assert.Equal(TableTuning.MinimumReadsInFlight, EnteredSetting.TableReadsInFlight(0));
        Assert.Equal(TableTuning.MaximumReadsInFlight, EnteredSetting.TableReadsInFlight(1000));
        Assert.Equal(TableTuning.MinimumParseThreads, EnteredSetting.TableParseThreads(-3));
        Assert.Equal(TableTuning.MaximumParseThreads, EnteredSetting.TableParseThreads(1000));
        Assert.Equal(6, EnteredSetting.TableParseThreads(5.5));
    }

    private static ScanTuner Tuner(ICurrentPreferences preferences, FakeStorageQueries queries, IVolumeInventory volumes) =>
        new(preferences, new VolumeMediaCache(queries), volumes);
}
