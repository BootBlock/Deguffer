using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the user is told before a source folder is approved. The wording is asserted because it is
/// what the decision is made on: the reader is being asked to spend a download of everything they keep
/// on a drive, and a sentence that only said "this may be slow" would get a yes for the wrong reason.
/// </summary>
public sealed class SourceRootApprovalTests
{
    /// <summary>The flag word measured on a Google Drive mount, against a local NTFS volume's.</summary>
    private const VolumeFeatures CloudMount = (VolumeFeatures)0x0000_0106;

    private const VolumeFeatures LocalDisk = (VolumeFeatures)0x03E7_2EFF;

    private static readonly FakeVolumeInventory Machine = new FakeVolumeInventory()
        .With(@"C:\", features: LocalDisk)
        .With(@"V:\", features: CloudMount);

    [Fact]
    public void WarnsAboutAFolderOnACloudMount()
    {
        var approval = SourceRootApproval.For(Machine, @"V:\work");

        Assert.True(approval.NeedsConfirming);
        Assert.Equal(SourceRootApproval.RemoteStorageWarning, approval.Warning);
    }

    [Fact]
    public void SaysNothingAboutAFolderOnAnOrdinaryDisk()
    {
        var approval = SourceRootApproval.For(Machine, @"C:\Users\testuser\src");

        Assert.False(approval.NeedsConfirming);
        Assert.Null(approval.Warning);
    }

    /// <summary>
    /// Nothing measured a share's flags, and warning on no reading would be a guess — the same
    /// non-answer <see cref="HostVolume.For"/> gives.
    /// </summary>
    [Fact]
    public void SaysNothingAboutAFolderOnAVolumeItKnowsNothingAbout()
    {
        Assert.False(SourceRootApproval.For(Machine, @"\\server\share\work").NeedsConfirming);
    }

    /// <summary>
    /// The approval reaches storage only where the warning was built, which is what ties the stored
    /// flag to the sentence that explains it. A folder on an ordinary disk stores no approval, because
    /// it needs none and a true there would be a consent nobody was asked for.
    /// </summary>
    [Fact]
    public void StoresTheApprovalOnlyForTheFolderThatWasWarnedAbout()
    {
        Assert.Equal(
            new SourceRoot(@"V:\work", RemoteStorageApproved: true),
            SourceRootApproval.For(Machine, @"V:\work").Accepted());

        Assert.Equal(
            new SourceRoot(@"C:\Users\testuser\src"),
            SourceRootApproval.For(Machine, @"C:\Users\testuser\src").Accepted());
    }

    /// <summary>
    /// The warning states the download and states what the deletion would then free, because the
    /// second half is the part a reader cannot work out for themselves: removing build output on a
    /// cloud mount frees space in the cloud, and the disk they are cleaning is unchanged. That is §5.4
    /// in a second shape.
    /// </summary>
    [Fact]
    public void TheWarningNamesBothHalvesOfTheCost()
    {
        Assert.Contains("downloads every file", SourceRootApproval.RemoteStorageWarning);
        Assert.Contains("space in the cloud rather than space on this disk", SourceRootApproval.RemoteStorageWarning);
    }
}
