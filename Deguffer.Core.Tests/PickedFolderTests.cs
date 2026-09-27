using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether a folder chosen in the system picker is one on a disk. The picker's result is a string
/// with no promise of a path in it, so anything that is not a fully qualified path is refused.
/// </summary>
public sealed class PickedFolderTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::{031E4825-7B94-4DC3-B131-E946B44C8DD5}\\Documents.library-ms")]
    [InlineData("This PC\\Phone\\Internal storage")]
    [InlineData("Games")]
    [InlineData(@"C:Games")]
    public void AChoiceThatIsNotAFullyQualifiedPathIsNothing(string picked) =>
        Assert.Null(PickedFolder.OnDisk(picked));

    [Theory]
    [InlineData(@"C:\Users\testuser\Games")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server.test\share\src")]
    public void AFolderOnADiskIsThatFolder(string picked) =>
        Assert.Equal(picked, PickedFolder.OnDisk(picked));

    /// <summary>
    /// A folder whose name ends in a dot or a space is a different folder from the one without it,
    /// and resolving the path would turn the one into the other.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\testuser\build.")]
    [InlineData(@"C:\Users\testuser\build ")]
    public void AFolderNamedWithATrailingDotOrSpaceKeepsIt(string picked) =>
        Assert.Equal(picked, PickedFolder.OnDisk(picked));
}
