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
}
