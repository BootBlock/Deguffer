using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

public sealed class VolumeDevicePathTests
{
    private const string Root = @"\Device\HarddiskVolume3\Users\testuser\OneDrive";

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Users\testuser\OneDrive")]
    [InlineData(@"\DEVICE\HARDDISKVOLUME3\USERS\TESTUSER\ONEDRIVE")]
    [InlineData(@"\Device\HarddiskVolume3\Users\testuser\OneDrive\")]
    public void NamesThePlaceWhateverItsCaseOrTrailingSeparator(string spelling)
    {
        var path = new VolumeDevicePath(spelling);

        Assert.Equal(new VolumeDevicePath(Root), path);
        Assert.Equal(new VolumeDevicePath(Root).GetHashCode(), path.GetHashCode());
    }

    /// <summary>The same folders on another volume, as a junction to that volume would lead to, are another place.</summary>
    [Fact]
    public void TheSameFoldersOnAnotherVolumeAreAnotherPlace() =>
        Assert.NotEqual(
            new VolumeDevicePath(Root),
            new VolumeDevicePath(@"\Device\HarddiskVolume4\Users\testuser\OneDrive"));

    [Theory]
    [InlineData(Root)]
    [InlineData(Root + @"\")]
    public void JoinsARelativePathWithOneSeparator(string root) =>
        Assert.Equal(
            Root + @"\Documents\report.docx",
            new VolumeDevicePath(root).Join(@"Documents\report.docx").Value);
}
