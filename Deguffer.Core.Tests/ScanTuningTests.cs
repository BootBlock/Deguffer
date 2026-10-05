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

    [Fact]
    public void AutoIsWhatScansUsedBeforeTheValuesCouldBeSetOnADriveOfUnknownKind()
    {
        var tuning = VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Unknown);

        Assert.Equal(WalkTuning.Default, tuning.Walk);
        Assert.Equal(TableTuning.Default, tuning.Table);
        Assert.Equal(MediaScanPreferences.Auto, tuning.Chosen);
    }

    /// <summary>
    /// The one value measured to differ by kind: solid state reads the table fastest in the largest
    /// reads, and a kind not measured keeps the size every scan used before.
    /// </summary>
    [Theory]
    [InlineData(StorageMedia.Nvme, TableTuning.MaximumReadBytes)]
    [InlineData(StorageMedia.SolidState, TableTuning.MaximumReadBytes)]
    [InlineData(StorageMedia.Rotational, 1024 * 1024)]
    [InlineData(StorageMedia.Removable, 1024 * 1024)]
    [InlineData(StorageMedia.Network, 1024 * 1024)]
    [InlineData(StorageMedia.Virtual, 1024 * 1024)]
    public void AutoReadsTheTableInTheSizeMeasuredForTheKindOfDrive(StorageMedia media, int readBytes)
    {
        var tuning = VolumeTuning.Resolve(ScanPreferences.Default, media);

        Assert.Equal(readBytes, tuning.Table.ReadBytes);
        Assert.Equal(WalkTuning.Default, tuning.Walk);
    }

    [Fact]
    public void AChosenValueIsUsedForItsOwnKindOfDriveAndNoOther()
    {
        var preferences = ScanPreferences.Default.With(
            StorageMedia.Rotational, new MediaScanPreferences(WalkThreads: 2, ListingBufferKiB: 64, TableReadKiB: 256));

        var spinning = VolumeTuning.Resolve(preferences, StorageMedia.Rotational);
        var nvme = VolumeTuning.Resolve(preferences, StorageMedia.Nvme);

        Assert.Equal(new WalkTuning(2, 64 * 1024), spinning.Walk);
        Assert.Equal(new TableTuning(256 * 1024), spinning.Table);
        Assert.Equal(VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Nvme), nvme);
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
            Unknown = new MediaScanPreferences(threads, listingKiB, tableKiB),
        };

        var tuning = VolumeTuning.Resolve(preferences, StorageMedia.Unknown);

        Assert.Equal(threads > 0 ? WalkTuning.MaximumThreads : WalkTuning.MinimumThreads, tuning.Walk.Threads);
        Assert.Equal(
            listingKiB > 0 ? WalkTuning.MaximumListingBuffer : WalkTuning.MinimumListingBuffer,
            tuning.Walk.ListingBufferBytes);
        Assert.Equal(
            tableKiB > 0 ? TableTuning.MaximumReadBytes : TableTuning.MinimumReadBytes,
            tuning.Table.ReadBytes);
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

    /// <summary>A path on no volume the inventory lists gets the conservative values.</summary>
    [Fact]
    public void APathOnNoKnownVolumeIsOfUnknownKind()
    {
        var tuner = Tuner(new FakePreferences(AppPreferences.Default), new FakeStorageQueries(), new FakeVolumeInventory());

        Assert.Equal(StorageMedia.Unknown, tuner.For(@"Q:\cache").Media);
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
        Assert.Empty(ScanTuner.Shipped.Drives());
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
            tuner.Drives());
    }

    [Fact]
    public void ADrivesLineSaysWhichValuesAutoChose()
    {
        var preferences = ScanPreferences.Default.With(StorageMedia.Nvme, new MediaScanPreferences(TableReadKiB: 4096));

        var line = ScanTuningText.Describe(new DriveKind("C:", StorageMedia.Nvme), preferences);
        var auto = VolumeTuning.Resolve(ScanPreferences.Default, StorageMedia.Nvme);

        Assert.StartsWith("C: (NVMe SSD): ", line, StringComparison.Ordinal);
        Assert.Contains($"{auto.Walk.Threads} threads (Auto)", line, StringComparison.Ordinal);
        Assert.Contains($"{auto.Walk.ListingBufferBytes / 1024} KiB listing buffer (Auto)", line, StringComparison.Ordinal);
        Assert.Contains($"{4096:N0} KiB table reads", line, StringComparison.Ordinal);
        Assert.DoesNotContain("table reads (Auto)", line, StringComparison.Ordinal);
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

        Assert.Equal(8, EnteredSetting.WalkThreads(7.5));
        Assert.Equal(WalkTuning.MinimumThreads, EnteredSetting.WalkThreads(0));
        Assert.Equal(WalkTuning.MaximumThreads, EnteredSetting.WalkThreads(1000));
        Assert.Equal(WalkTuning.MinimumListingBuffer / 1024, EnteredSetting.ListingBufferKiB(1));
        Assert.Equal(WalkTuning.MaximumListingBuffer / 1024, EnteredSetting.ListingBufferKiB(double.PositiveInfinity));
        Assert.Equal(TableTuning.MinimumReadBytes / 1024, EnteredSetting.TableReadKiB(double.NegativeInfinity));
        Assert.Equal(TableTuning.MaximumReadBytes / 1024, EnteredSetting.TableReadKiB(1e9));
    }

    private static ScanTuner Tuner(ICurrentPreferences preferences, FakeStorageQueries queries, IVolumeInventory volumes) =>
        new(preferences, new VolumeMediaCache(queries), volumes);
}
