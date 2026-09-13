using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one rule <see cref="LocalVolume"/> states: whether a volume's contents are somewhere else,
/// so that enumerating it downloads the user's files rather than measuring them.
///
/// <para>The two cases the rule exists to tell apart are asserted against the flag words a real
/// machine reported, because the reading is the evidence: a discrimination between two invented
/// inputs would prove the expression and not the discrimination. The cases either side of those
/// two have no measurement to stand on and name their bits directly.</para>
/// </summary>
public sealed class LocalVolumeTests
{
    /// <summary>
    /// A Google Drive mount, measured: <c>DRIVE_FIXED</c>, ready, FAT32, 100 GB, and
    /// indistinguishable from a disk by every per-entry test — its top-level entries carry ordinary
    /// hidden, system and normal attributes and no reparse point.
    /// </summary>
    private const VolumeFeatures GoogleDriveMount = (VolumeFeatures)0x0000_0106;

    /// <summary>A local NTFS volume, measured on seven of them on the same machine.</summary>
    private const VolumeFeatures LocalNtfsVolume = (VolumeFeatures)0x03E7_2EFF;

    [Fact]
    public void ACloudMountPresentedAsADriveLetterStoresItsContentRemotely()
    {
        var volume = new LocalVolume(@"V:\", DriveType.Fixed, IsReady: true, Features: GoogleDriveMount);

        Assert.True(volume.StoresContentRemotely);
    }

    /// <summary>
    /// The case the discrimination exists for. Both volumes are fixed, ready and have a label and a
    /// size, so nothing else about the reading tells them apart.
    /// </summary>
    [Fact]
    public void AnOrdinaryLocalVolumeDoesNot()
    {
        var volume = new LocalVolume(@"C:\", DriveType.Fixed, IsReady: true, Features: LocalNtfsVolume);

        Assert.False(volume.StoresContentRemotely);
    }

    /// <summary>
    /// Remote storage alone is not the test. NTFS can carry the flag while still being an ordinary
    /// local volume, and it then supports reparse points too — which is what the pair excludes. A
    /// volume that can hold a reparse point can hold a Cloud Files placeholder, so its offline
    /// content is classified per entry and the walk can defend itself without refusing the drive.
    /// </summary>
    [Fact]
    public void AVolumeThatSupportsReparsePointsAsWellIsNotRefused()
    {
        var volume = new LocalVolume(
            @"C:\",
            DriveType.Fixed,
            IsReady: true,
            Features: VolumeFeatures.RemoteStorage | VolumeFeatures.ReparsePoints);

        Assert.False(volume.StoresContentRemotely);
    }

    /// <summary>
    /// A volume that would not answer is walked, which is what every volume did before this reading
    /// existed.
    ///
    /// <para><b>This fails open, and the risk is real rather than notional.</b> The mount
    /// announces itself in the flag word and nowhere else — that is the whole premise of the rule —
    /// so a volume whose flag word did not come back is a cloud mount Deguffer cannot recognise.
    /// The alternative is worse: one failing call would refuse every volume on the machine, which
    /// is the tool's whole function traded against a hazard that call gave no evidence of.</para>
    /// </summary>
    [Fact]
    public void AVolumeThatSaidNothingIsNotRefused()
    {
        Assert.False(new LocalVolume(@"E:\", DriveType.Fixed, IsReady: true).StoresContentRemotely);
    }
}
