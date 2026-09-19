using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Explore drive picker's entries. What is asserted is the wording and the arithmetic, because
/// those are what a reader acts on: an entry that understates what is free, or that shows a zero
/// where the volume said nothing, sends somebody to scan the wrong disk.
/// </summary>
public sealed class DriveChoiceTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Fact]
    public void CarriesWhatTheMachineReportedAboutTheVolume()
    {
        var choice = DriveChoice.From(new LocalVolume(
            @"D:\", DriveType.Fixed, IsReady: true, Label: "Projects",
            TotalBytes: 100 * Gigabyte, FreeBytes: 40 * Gigabyte));

        Assert.Equal(@"D:\", choice.RootPath);
        Assert.Equal("Projects", choice.Label);
        Assert.Equal(100 * Gigabyte, choice.TotalBytes);
        Assert.Equal(40 * Gigabyte, choice.FreeBytes);
    }

    /// <summary>
    /// Derived from the two figures it is shown beside, so the three can never contradict each
    /// other on screen.
    /// </summary>
    [Fact]
    public void UsedSpaceIsWhatCapacityLessFreeSpaceLeaves()
    {
        var choice = new DriveChoice(@"C:\", "Windows", 100 * Gigabyte, 40 * Gigabyte);

        Assert.Equal(60 * Gigabyte, choice.UsedBytes);
    }

    [Fact]
    public void StatesUsedFreeAndCapacityTogether()
    {
        var choice = new DriveChoice(@"C:\", "Windows", 100 * Gigabyte, 40 * Gigabyte);

        Assert.Equal("60.0 GB used, 40.0 GB free of 100 GB", choice.Sizes);
    }

    /// <summary>
    /// A volume that would not answer says so. A zero here would be read as a full disk, and a
    /// dash as an empty one.
    /// </summary>
    [Fact]
    public void SaysTheSizeIsUnknownRatherThanShowingAZero()
    {
        var choice = DriveChoice.From(new LocalVolume(@"E:\", DriveType.Removable, IsReady: true));

        Assert.Null(choice.UsedBytes);
        Assert.Equal("size unknown", choice.Sizes);
    }

    /// <summary>
    /// Either figure alone is not enough to word the phrase, and half of it would be worse than
    /// none: "40 GB free of 0 B" reads as a fault in the app rather than in the reading.
    /// </summary>
    [Fact]
    public void SaysTheSizeIsUnknownWhenOnlyOneFigureCameBack()
    {
        Assert.Equal("size unknown", new DriveChoice(@"E:\", null, 100 * Gigabyte, null).Sizes);
        Assert.Equal("size unknown", new DriveChoice(@"E:\", null, null, 40 * Gigabyte).Sizes);
    }

    /// <summary>A binding cannot show null, and an unlabelled volume is common.</summary>
    [Fact]
    public void AnUnlabelledVolumeShowsNoLabelRatherThanTheWordNull()
    {
        Assert.Equal(string.Empty, new DriveChoice(@"E:\", null, null, null).LabelText);
        Assert.Equal("Projects", new DriveChoice(@"D:\", "Projects", null, null).LabelText);
    }

    /// <summary>
    /// The whole entry in one sentence, because a templated combo box item is otherwise announced
    /// as its parts in layout order with nothing between them.
    /// </summary>
    [Fact]
    public void ReadsAsOneSentenceForAScreenReader()
    {
        var labelled = new DriveChoice(@"C:\", "Windows", 100 * Gigabyte, 40 * Gigabyte);
        Assert.Equal(@"C:\ Windows, 60.0 GB used, 40.0 GB free of 100 GB", labelled.Description);

        // No stray separator where there is no label to separate.
        Assert.Equal(@"E:\, size unknown", new DriveChoice(@"E:\", null, null, null).Description);
    }

    /// <summary>
    /// A cloud mount is listed with a sentence saying it will not be scanned. Listed, because a
    /// drive the user can see in File Explorer and cannot find here is one they cannot reason about
    /// — and refused, because walking it downloads what is on it.
    /// </summary>
    [Fact]
    public void ACloudMountIsOfferedAndRefused()
    {
        var choice = DriveChoice.From(new LocalVolume(
            @"V:\", DriveType.Fixed, IsReady: true, Label: "Google Drive",
            Features: (VolumeFeatures)0x0000_0106));

        Assert.True(choice.IsRefused);
        Assert.Equal(DriveChoice.RemoteStorageRefusal, choice.Refusal);

        // Still an ordinary entry in every other respect. Its label and its size are true, and
        // hiding them would make the row harder to recognise as the drive the sentence is about.
        Assert.Equal("Google Drive", choice.Label);
    }

    /// <summary>
    /// The refusal is announced with the row rather than beside it. A reader who hears the mount
    /// point and the sizes, and then finds Scan unavailable, has been told the two things that do
    /// not matter and none of the one that does.
    /// </summary>
    [Fact]
    public void ARefusedEntryAnnouncesWhyItCannotBeScanned()
    {
        var refused = new DriveChoice(@"V:\", "Google Drive", null, null, DriveChoice.RemoteStorageRefusal);

        Assert.Equal($@"V:\ Google Drive, size unknown. {DriveChoice.RemoteStorageRefusal}", refused.Description);
    }

    [Fact]
    public void AnOrdinaryVolumeIsNotRefused()
    {
        var choice = DriveChoice.From(new LocalVolume(
            @"C:\", DriveType.Fixed, IsReady: true, Features: (VolumeFeatures)0x03E7_2EFF));

        Assert.False(choice.IsRefused);
        Assert.Null(choice.Refusal);
        Assert.Equal(@"C:\, size unknown", choice.Description);
    }

    /// <summary>
    /// A volume mounted at a folder is offered under that folder's name, like any other volume. The
    /// rules that keep a volume's paging file, its NTFS records and its Recycle Bin out of a
    /// deletion find the top of a volume wherever it is mounted, so scanning one mounted this way
    /// offers nothing they would not refuse on a drive.
    /// </summary>
    [Fact]
    public void AVolumeMountedAtAFolderIsOffered()
    {
        var choice = DriveChoice.From(new LocalVolume(
            @"C:\Mount\", DriveType.Fixed, IsReady: true, Label: "Archive",
            Features: (VolumeFeatures)0x03E7_2EFF));

        Assert.False(choice.IsRefused);
        Assert.Null(choice.Refusal);
        Assert.Equal(@"C:\Mount\", choice.RootPath);
        Assert.Equal("Archive", choice.Label);
    }

    /// <summary>
    /// A cloud volume is refused wherever it is mounted. Mounting one at a folder must not be a way
    /// round the refusal that stops every file the user keeps in the cloud being fetched onto the
    /// disk they are clearing.
    /// </summary>
    [Fact]
    public void ACloudVolumeMountedAtAFolderIsRefusedForTheDownload()
    {
        var choice = DriveChoice.From(new LocalVolume(
            @"C:\Mount\", DriveType.Fixed, IsReady: true, Features: (VolumeFeatures)0x0000_0106));

        Assert.Equal(DriveChoice.RemoteStorageRefusal, choice.Refusal);
    }
}
