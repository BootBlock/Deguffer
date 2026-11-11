using Deguffer.Core.Retrim;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which volumes a finished clean offers a retrim on, from invented volumes, free figures and storage
/// classes. Nothing here asks the machine.
/// </summary>
public sealed class RetrimOfferTests
{
    private const long GiB = 1L << 30;

    private const string C = @"C:\";
    private const string D = @"D:\";
    private const string NameOfC = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string NameOfD = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";

    private static LocalVolume Volume(string root, string? name, long? free, DriveType kind = DriveType.Fixed) =>
        new(root, kind, VolumeReadiness.Ready, FreeBytes: free, VolumeName: name);

    private static VolumeMedia On(StorageMedia media, bool? thin = false) => new(media, [0], ThinlyProvisioned: thin);

    private static IReadOnlyList<RetrimCandidate> Offer(LocalVolume before, LocalVolume after, VolumeMedia media) =>
        RetrimOffer.For([before], [after], _ => media);

    [Theory]
    [InlineData(StorageMedia.Nvme, RetrimBasis.SolidState)]
    [InlineData(StorageMedia.SolidState, RetrimBasis.SolidState)]
    [InlineData(StorageMedia.Virtual, RetrimBasis.VirtualDisk)]
    public void AVolumeOnSolidStateOrInAVirtualDiskIsOffered(StorageMedia media, RetrimBasis expected)
    {
        var offered = Offer(Volume(C, NameOfC, 10 * GiB), Volume(C, NameOfC, 13 * GiB), On(media));

        var candidate = Assert.Single(offered);
        Assert.Equal(expected, candidate.Basis);
        Assert.Equal(3 * GiB, candidate.Freed);
        Assert.Equal(C, candidate.Volume.RootPath);
    }

    /// <summary>
    /// Thin is offered whatever the class, a thin space on spinning disks and a thin disk of unknown
    /// class included, and it is the basis named even where the class would also qualify.
    /// </summary>
    [Theory]
    [InlineData(StorageMedia.Rotational)]
    [InlineData(StorageMedia.Unknown)]
    [InlineData(StorageMedia.SolidState)]
    public void AThinVolumeIsOfferedAsThinWhateverItsClass(StorageMedia media)
    {
        var offered = Offer(Volume(C, NameOfC, 0), Volume(C, NameOfC, 2 * GiB), On(media, thin: true));

        Assert.Equal(RetrimBasis.ThinlyProvisioned, Assert.Single(offered).Basis);
    }

    /// <summary>The unrecognised case: storage that is not known to benefit is offered nothing.</summary>
    [Theory]
    [InlineData(StorageMedia.Rotational, false)]
    [InlineData(StorageMedia.Rotational, null)]
    [InlineData(StorageMedia.Unknown, null)]
    [InlineData(StorageMedia.Removable, null)]
    [InlineData(StorageMedia.Network, null)]
    public void StorageNotKnownToBenefitIsNotOffered(StorageMedia media, bool? thin)
    {
        Assert.Empty(Offer(Volume(C, NameOfC, 0), Volume(C, NameOfC, 50 * GiB), On(media, thin)));
    }

    /// <summary>
    /// A removable volume is not offered even where its storage says solid state or thin: its bridge
    /// does not say what is behind it.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    public void OnlyAFixedVolumeIsOffered(DriveType kind)
    {
        var offered = Offer(Volume(D, NameOfD, 0, kind), Volume(D, NameOfD, 50 * GiB, kind), On(StorageMedia.SolidState, thin: true));

        Assert.Empty(offered);
    }

    [Fact]
    public void AVolumeWindowsDoesNotNameIsNotOffered()
    {
        Assert.Empty(Offer(Volume(C, null, 0), Volume(C, null, 50 * GiB), On(StorageMedia.SolidState)));
    }

    /// <summary>The threshold is exclusive: exactly 1 GiB is not more than it.</summary>
    [Theory]
    [InlineData(GiB, false)]
    [InlineData(GiB + 1, true)]
    [InlineData(-5 * GiB, false)]
    public void OnlyMoreThanTheThresholdIsOffered(long freed, bool offered)
    {
        var result = Offer(Volume(C, NameOfC, 10 * GiB), Volume(C, NameOfC, (10 * GiB) + freed), On(StorageMedia.Nvme));

        Assert.Equal(offered, result.Count == 1);
    }

    /// <summary>A growth from one reading alone would be a guess.</summary>
    [Fact]
    public void AVolumeWithNoFreeFigureOnEitherSideIsNotOffered()
    {
        Assert.Empty(Offer(Volume(C, NameOfC, null), Volume(C, NameOfC, 50 * GiB), On(StorageMedia.Nvme)));
        Assert.Empty(Offer(Volume(C, NameOfC, 0), Volume(C, NameOfC, null), On(StorageMedia.Nvme)));
    }

    /// <summary>
    /// A letter moved to another volume during the clean is not compared with the one it left: the
    /// volumes are matched by the name Windows knows them by.
    /// </summary>
    [Fact]
    public void VolumesAreMatchedByTheirNameNotTheirLetter()
    {
        var offered = RetrimOffer.For(
            [Volume(D, NameOfD, 0)],
            [Volume(D, NameOfC, 50 * GiB)],
            _ => On(StorageMedia.Nvme));

        Assert.Empty(offered);
    }

    /// <summary>The storage is asked only of a volume that freed enough, since each answer opens a device.</summary>
    [Fact]
    public void OnlyAVolumeThatFreedEnoughIsAskedAboutItsStorage()
    {
        List<string> asked = [];

        var offered = RetrimOffer.For(
            [Volume(C, NameOfC, 10 * GiB), Volume(D, NameOfD, 10 * GiB)],
            [Volume(C, NameOfC, 10 * GiB), Volume(D, NameOfD, 20 * GiB)],
            volume =>
            {
                asked.Add(volume.RootPath);
                return On(StorageMedia.SolidState);
            });

        Assert.Equal([D], asked);
        Assert.Equal(D, Assert.Single(offered).Volume.RootPath);
    }

    /// <summary>
    /// The readings either side are live: the inventory's own list is remembered from when it was last
    /// read, and its figure would show the clean as freeing nothing.
    /// </summary>
    [Fact]
    public void TheFreeSpaceIsReadLiveForFixedNamedVolumesOnly()
    {
        var inventory = new FakeVolumeInventory()
            .With(C, totalBytes: 100 * GiB, freeBytes: 7 * GiB, volumeName: NameOfC)
            .With(D, totalBytes: 100 * GiB, freeBytes: 1 * GiB)
            .With(@"E:\", kind: DriveType.Removable, totalBytes: 100 * GiB, freeBytes: 1 * GiB, volumeName: NameOfD);

        var reading = FreeSpaceReading.Take(inventory);

        var only = Assert.Single(reading);
        Assert.Equal(C, only.RootPath);
        Assert.Equal(7 * GiB, only.FreeBytes);
    }
}
