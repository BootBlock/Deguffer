using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The class each volume is given, from synthesised descriptors. Nothing here asks the machine:
/// which buses and disks the machine running the suite has is not a property of this code.
/// </summary>
public sealed class VolumeMediaTests
{
    private const string Root = @"C:\";

    private const byte Sata = 0x0B;
    private const byte Sas = 0x0A;
    private const byte Nvme = 0x11;
    private const byte Usb = 0x07;
    private const byte Sd = 0x0C;
    private const byte Mmc = 0x0D;
    private const byte Ieee1394 = 0x04;
    private const byte IScsi = 0x09;
    private const byte Virtual = 0x0E;
    private const byte FileBackedVirtual = 0x0F;
    private const byte Spaces = 0x10;
    private const int ErrorAccessDenied = 5;

    private static LocalVolume Fixed(string root = Root) => new(root, DriveType.Fixed, VolumeReadiness.Ready);

    private static VolumeMedia Classify(FakeStorageQueries queries, LocalVolume? volume = null) =>
        new VolumeMediaCache(queries).Of(volume ?? Fixed());

    /// <summary>
    /// The bus decides before the seek penalty is asked: an NVMe drive is classed as NVMe even where
    /// it would say it has a seek penalty, and the question is not sent at all.
    /// </summary>
    [Fact]
    public void AnNvmeBusIsNvmeWithoutAskingTheSeekPenalty()
    {
        var queries = new FakeStorageQueries().Volume(Root, 1).Disk(1, Nvme, seekPenalty: true);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Nvme, media.Class);
        Assert.Equal([1], media.PhysicalDisks);
        Assert.Equal(0, queries.SeekPenaltiesAsked);
    }

    [Theory]
    [InlineData(Sata, false, StorageMedia.SolidState)]
    [InlineData(Sata, true, StorageMedia.Rotational)]
    [InlineData(Sas, true, StorageMedia.Rotational)]
    [InlineData(Spaces, false, StorageMedia.SolidState)]
    [InlineData(Spaces, true, StorageMedia.Rotational)]
    public void AnyOtherBusIsClassedByItsSeekPenalty(byte bus, bool seekPenalty, StorageMedia expected)
    {
        var queries = new FakeStorageQueries().Volume(Root, 0).Disk(0, bus, seekPenalty);

        var media = Classify(queries);

        Assert.Equal(expected, media.Class);
        Assert.Equal(MediaUnknownReason.None, media.Reason);
    }

    [Theory]
    [InlineData(Usb, StorageMedia.Removable)]
    [InlineData(Sd, StorageMedia.Removable)]
    [InlineData(Mmc, StorageMedia.Removable)]
    [InlineData(Ieee1394, StorageMedia.Removable)]
    [InlineData(IScsi, StorageMedia.Network)]
    [InlineData(Virtual, StorageMedia.Virtual)]
    [InlineData(FileBackedVirtual, StorageMedia.Virtual)]
    public void ABusThatDecidesTheClassIsNotOverruledByTheSeekPenalty(byte bus, StorageMedia expected)
    {
        // A seek penalty of false would read as solid state if the bus were ignored.
        var queries = new FakeStorageQueries().Volume(Root, 3).Disk(3, bus, seekPenalty: false);

        Assert.Equal(expected, Classify(queries).Class);
    }

    /// <summary>
    /// A Storage Spaces disk that gives no seek-penalty answer is unknown. The bus says nothing about
    /// the disks in the pool, so solid state would be a guess.
    /// </summary>
    [Fact]
    public void AStorageSpacesDiskWithNoSeekPenaltyAnswerIsUnknown()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 4)
            .DiskAnswers(4, StorageAnswer.Answered(FakeStorageQueries.AdapterBytes(Spaces)), StorageAnswer.Failed(ErrorAccessDenied));

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.SeekPenaltyNotRead, media.Reason);
        Assert.Equal(ErrorAccessDenied, media.Win32Error);
        Assert.Equal([4], media.PhysicalDisks);
    }

    [Fact]
    public void AShareIsNetworkWithoutAskingAnything()
    {
        var queries = new FakeStorageQueries();

        var media = Classify(queries, new LocalVolume(@"N:\", DriveType.Network, VolumeReadiness.Ready));

        Assert.Equal(StorageMedia.Network, media.Class);
        Assert.Empty(media.PhysicalDisks);
        Assert.Equal(0, queries.ExtentsAsked + queries.AdaptersAsked + queries.SeekPenaltiesAsked);
    }

    [Fact]
    public void AnOpticalDriveIsRemovableWithoutAskingAnything()
    {
        var queries = new FakeStorageQueries();

        var media = Classify(queries, new LocalVolume(@"R:\", DriveType.CDRom, VolumeReadiness.NoMedia));

        Assert.Equal(StorageMedia.Removable, media.Class);
        Assert.Equal(0, queries.ExtentsAsked);
    }

    [Theory]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    public void AMountPointWithNoKindIsUnknown(DriveType kind)
    {
        var media = Classify(new FakeStorageQueries().Volume(Root, 0).Disk(0, Nvme, false), new LocalVolume(Root, kind, VolumeReadiness.Ready));

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.KindNotReported, media.Reason);
    }

    /// <summary>
    /// A removable volume keeps its class when the extents fail, because its kind already said what
    /// it is, and reports its disk where the extents answer, so a caller can tell which volumes share
    /// that disk. The bus is not asked: the kind decided.
    /// </summary>
    [Fact]
    public void ARemovableVolumeIsRemovableAndStillNamesItsDisk()
    {
        var removable = new LocalVolume(@"F:\", DriveType.Removable, VolumeReadiness.Ready);
        var answered = new FakeStorageQueries().Volume(@"F:\", 6).Disk(6, Nvme, false);

        var media = Classify(answered, removable);

        Assert.Equal(StorageMedia.Removable, media.Class);
        Assert.Equal([6], media.PhysicalDisks);
        Assert.Equal(0, answered.AdaptersAsked);

        var unanswered = Classify(new FakeStorageQueries(), removable);

        Assert.Equal(StorageMedia.Removable, unanswered.Class);
        Assert.Empty(unanswered.PhysicalDisks);
    }

    [Fact]
    public void AVolumeThatWillNotNameItsDisksIsUnknownWithTheError()
    {
        var media = Classify(new FakeStorageQueries().VolumeAnswer(Root, StorageAnswer.Failed(ErrorAccessDenied)));

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.ExtentsNotRead, media.Reason);
        Assert.Equal(ErrorAccessDenied, media.Win32Error);
        Assert.Empty(media.PhysicalDisks);
    }

    /// <summary>
    /// A count that promises more extents than the bytes hold is a malformed answer. Reading the
    /// missing extents as disk 0 would class the volume by a disk that may not hold it.
    /// </summary>
    [Fact]
    public void ExtentsShorterThanTheirCountAreNotRead()
    {
        var truncated = FakeStorageQueries.ExtentBytes(1, 2)[..^24];
        var queries = new FakeStorageQueries()
            .VolumeAnswer(Root, StorageAnswer.Answered(truncated))
            .Disk(0, Nvme, false)
            .Disk(1, Nvme, false);

        var media = Classify(queries);

        Assert.Equal(MediaUnknownReason.ExtentsNotRead, media.Reason);
        Assert.Equal(0, media.Win32Error);
    }

    [Fact]
    public void AVolumeThatNamesNoDiskIsUnknown()
    {
        var media = Classify(new FakeStorageQueries().Volume(Root));

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.NoDiskBehindVolume, media.Reason);
    }

    [Fact]
    public void ADiskThatWillNotNameItsBusIsUnknownWithTheError()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 2)
            .DiskAnswers(2, StorageAnswer.Failed(ErrorAccessDenied), StorageAnswer.Answered(FakeStorageQueries.SeekPenaltyBytes(false)));

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.AdapterNotRead, media.Reason);
        Assert.Equal(ErrorAccessDenied, media.Win32Error);
    }

    /// <summary>
    /// A descriptor that stops before the field it is read for has said nothing about that field.
    /// The adapter one is cut just before the bus byte, and the seek-penalty one just before the
    /// flag, so a reader that took a zero there would class the disk anyway.
    /// </summary>
    [Fact]
    public void DescriptorsShorterThanTheirFieldAreNotRead()
    {
        var shortAdapter = new FakeStorageQueries()
            .Volume(Root, 0)
            .DiskAnswers(0, StorageAnswer.Answered(FakeStorageQueries.AdapterBytes(Nvme)[..24]), StorageAnswer.Answered(FakeStorageQueries.SeekPenaltyBytes(false)));
        var shortSeekPenalty = new FakeStorageQueries()
            .Volume(Root, 0)
            .DiskAnswers(0, StorageAnswer.Answered(FakeStorageQueries.AdapterBytes(Sata)), StorageAnswer.Answered(FakeStorageQueries.SeekPenaltyBytes(false)[..8]));

        Assert.Equal(MediaUnknownReason.AdapterNotRead, Classify(shortAdapter).Reason);
        Assert.Equal(MediaUnknownReason.SeekPenaltyNotRead, Classify(shortSeekPenalty).Reason);
    }

    /// <summary>
    /// A volume across several disks takes the class of its slowest disk, and names each disk once,
    /// in order, however many extents it has on each.
    /// </summary>
    [Fact]
    public void ASpannedVolumeTakesItsSlowestDisk()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 2, 0, 2, 1)
            .Disk(0, Nvme, false)
            .Disk(1, Sata, true)
            .Disk(2, Sata, false);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Rotational, media.Class);
        Assert.Equal([0, 1, 2], media.PhysicalDisks);
    }

    [Fact]
    public void ASpannedVolumeOfSolidStateAndNvmeIsSolidState()
    {
        var queries = new FakeStorageQueries().Volume(Root, 0, 1).Disk(0, Nvme, false).Disk(1, Sata, false);

        Assert.Equal(StorageMedia.SolidState, Classify(queries).Class);
    }

    /// <summary>One disk the volume cannot class makes the volume unknown: its slowest disk is then not known.</summary>
    [Fact]
    public void ASpannedVolumeWithAnUnknownDiskIsUnknown()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 0, 1, 2)
            .Disk(0, Nvme, false)
            .Disk(1, Sata, seekPenalty: null)
            .Disk(2, Sata, true);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.SeekPenaltyNotRead, media.Reason);
        Assert.Equal(FakeStorageQueries.ErrorFileNotFound, media.Win32Error);
    }

    [Fact]
    public void ASpannedVolumeOfUnrankedKindsIsUnknown()
    {
        var queries = new FakeStorageQueries().Volume(Root, 0, 1).Disk(0, Usb, false).Disk(1, FileBackedVirtual, false);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.Equal(MediaUnknownReason.DisksOfUnrankedKinds, media.Reason);
    }

    [Fact]
    public void ASpannedVolumeOfOneUnrankedKindKeepsIt()
    {
        var queries = new FakeStorageQueries().Volume(Root, 0, 1).Disk(0, Usb, false).Disk(1, Usb, true);

        Assert.Equal(StorageMedia.Removable, Classify(queries).Class);
    }

    /// <summary>
    /// Each volume and each disk is asked once for the operation's life (G4), so two letters on one
    /// disk ask that disk once, and <see cref="VolumeMediaCache.Invalidate"/> makes the next operation
    /// ask again.
    /// </summary>
    [Fact]
    public void EachVolumeAndEachDiskIsAskedOnceUntilInvalidated()
    {
        var queries = new FakeStorageQueries()
            .Volume(@"C:\", 1)
            .Volume(@"P:\", 1)
            .Disk(1, Sata, false);
        var cache = new VolumeMediaCache(queries);

        cache.Of(Fixed(@"C:\"));
        cache.Of(Fixed(@"c:\"));
        cache.Of(Fixed(@"P:\"));

        Assert.Equal(2, queries.ExtentsAsked);
        Assert.Equal(1, queries.AdaptersAsked);
        Assert.Equal(1, queries.SeekPenaltiesAsked);

        cache.Invalidate();
        cache.Of(Fixed(@"C:\"));

        Assert.Equal(3, queries.ExtentsAsked);
        Assert.Equal(2, queries.AdaptersAsked);
    }
}
