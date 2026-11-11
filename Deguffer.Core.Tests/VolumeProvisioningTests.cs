using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a volume is thinly provisioned, from synthesised provisioning descriptors. Asked apart
/// from the class, so neither answer decides the other, and a disk that will not say is never read
/// as one that is not thin.
/// </summary>
public sealed class VolumeProvisioningTests
{
    private const string Root = @"D:\";

    private const byte Sata = 0x0B;
    private const byte Spaces = 0x10;

    private static VolumeMedia Classify(FakeStorageQueries queries, DriveType kind = DriveType.Fixed) =>
        new VolumeMediaCache(queries).Of(new LocalVolume(Root, kind, VolumeReadiness.Ready));

    /// <summary>
    /// A thin Storage Spaces disk on spinning disks: rotational by its seek penalty, and thin by its
    /// own answer. The class alone would hide the one fact a retrim needs.
    /// </summary>
    [Fact]
    public void AThinDiskIsThinWhateverItsClass()
    {
        var queries = new FakeStorageQueries().Volume(Root, 2).Disk(2, Spaces, seekPenalty: true).Thin(2, true);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Rotational, media.Class);
        Assert.True(media.ThinlyProvisioned);
    }

    [Fact]
    public void ADiskThatSaysItIsNotThinIsNot()
    {
        var queries = new FakeStorageQueries().Volume(Root, 0).Disk(0, Sata, seekPenalty: false).Thin(0, false);

        Assert.False(Classify(queries).ThinlyProvisioned);
    }

    /// <summary>A refusal is not an answer of "not thin", and it costs the disk none of its class.</summary>
    [Fact]
    public void ADiskThatWillNotSayIsNotKnownAndKeepsItsClass()
    {
        var queries = new FakeStorageQueries().Volume(Root, 0).Disk(0, Sata, seekPenalty: false).Thin(0, null);

        var media = Classify(queries);

        Assert.Null(media.ThinlyProvisioned);
        Assert.Equal(StorageMedia.SolidState, media.Class);
        Assert.Equal(MediaUnknownReason.None, media.Reason);
    }

    /// <summary>
    /// A disk whose class is unknown can still say it is thin, which is the answer a thin disk behind
    /// a storage controller that gives no seek penalty has.
    /// </summary>
    [Fact]
    public void ADiskOfUnknownClassCanStillBeThin()
    {
        var queries = new FakeStorageQueries().Volume(Root, 4).Disk(4, Spaces, seekPenalty: null).Thin(4, true);

        var media = Classify(queries);

        Assert.Equal(StorageMedia.Unknown, media.Class);
        Assert.True(media.ThinlyProvisioned);
    }

    /// <summary>
    /// Spanned over a disk that will not say and one that is thin: thin, as clusters freed on the
    /// thin one go back only once trimmed. The unknown disk is asked too, although it comes first and
    /// makes the class unknown.
    /// </summary>
    [Fact]
    public void AVolumeIsThinWhereAnyDiskBehindItIs()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 1, 5)
            .Disk(1, Sata, seekPenalty: null).Thin(1, null)
            .Disk(5, Sata, seekPenalty: false).Thin(5, true);

        var media = Classify(queries);

        Assert.True(media.ThinlyProvisioned);
        Assert.Equal(2, queries.ProvisioningAsked);
    }

    [Fact]
    public void AVolumeWithOneDiskThatWillNotSayAndNoneThinIsNotKnown()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 1, 5)
            .Disk(1, Sata, seekPenalty: false).Thin(1, false)
            .Disk(5, Sata, seekPenalty: false).Thin(5, null);

        Assert.Null(Classify(queries).ThinlyProvisioned);
    }

    /// <summary>A share and a removable volume are not asked, so they are not known to be anything.</summary>
    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Removable)]
    public void AVolumeWhoseDisksAreNotAskedIsNotKnown(DriveType kind)
    {
        var queries = new FakeStorageQueries().Volume(Root, 0).Disk(0, Sata, seekPenalty: false).Thin(0, true);

        Assert.Null(Classify(queries, kind).ThinlyProvisioned);
        Assert.Equal(0, queries.ProvisioningAsked);
    }

    /// <summary>
    /// The flag is the lowest bit of the byte after the header. The fake sets every other bit of that
    /// byte, so a read of the wrong bit, or of the whole byte, answers the other way.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFlagIsReadFromItsOwnBit(bool thin)
    {
        Assert.True(StorageDescriptors.TryReadThinProvisioning(FakeStorageQueries.ProvisioningBytes(thin), out var read));
        Assert.Equal(thin, read);
    }

    /// <summary>A descriptor cut short before the flag is a refusal to say, not an answer of "not thin".</summary>
    [Fact]
    public void ADiskWhoseAnswerStopsBeforeTheFlagIsNotKnown()
    {
        var queries = new FakeStorageQueries()
            .Volume(Root, 0)
            .Disk(0, Sata, seekPenalty: false)
            .ProvisioningAnswer(0, StorageAnswer.Answered(FakeStorageQueries.ProvisioningBytes(thin: true)[..8]));

        Assert.Null(Classify(queries).ThinlyProvisioned);
    }

    [Fact]
    public void AnAnswerTooShortToHoldTheFlagSaysNothing()
    {
        Assert.False(StorageDescriptors.TryReadThinProvisioning(new byte[8], out _));
    }
}
