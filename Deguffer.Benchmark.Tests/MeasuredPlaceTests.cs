namespace Deguffer.Benchmark.Tests;

/// <summary>
/// A result names the drive and never the path, so it can be pasted into a public issue as it
/// stands.
/// </summary>
public sealed class MeasuredPlaceTests
{
    [Theory]
    [InlineData(@"C:\Users\testuser\project", "C:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"\\?\C:\Users\testuser\project", "C:")]
    public void APathOnADriveIsNamedByItsLetterAlone(string path, string root) =>
        Assert.Equal(root, MeasuredPlace.RootOf(path));

    [Theory]
    [InlineData(@"\\fileserver.test\testuser\project")]
    [InlineData(@"\\?\UNC\fileserver.test\testuser\project")]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\testuser")]
    public void APathWithNoDriveLetterNamesNothingOfIt(string path)
    {
        Assert.Equal(MeasuredPlace.NoLetter, MeasuredPlace.RootOf(path));

        // Built without asking Windows, because there is no drive to ask about.
        var described = MeasuredPlace.Of(path).ToString();

        Assert.DoesNotContain("fileserver", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testuser", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Volume{", described, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\fileserver.test\share\project", DriveType.Network)]
    [InlineData(@"\\?\UNC\fileserver.test\share\project", DriveType.Network)]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\folder", DriveType.Unknown)]
    public void AShareIsANetworkDriveAndAVolumeByGuidIsUnknown(string path, DriveType kind) =>
        Assert.Equal(kind, MeasuredPlace.Of(path).Kind);
}
