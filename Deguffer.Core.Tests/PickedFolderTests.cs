using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a folder chosen in the system picker becomes. The picker lets a person choose places that
/// are not folders on a disk, and what comes back for them is not a path Deguffer can use.
/// </summary>
public sealed class PickedFolderTests
{
    /// <summary>
    /// A library, a phone or a namespace extension comes back as an empty path or a shell name, and
    /// none of them is a folder on a disk.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::{031E4825-7B94-4DC3-B131-E946B44C8DD5}\\Documents.library-ms")]
    [InlineData("This PC\\Phone\\Internal storage")]
    [InlineData("Games")]
    public void AChoiceThatIsNotAFolderOnADiskIsNothing(string picked) =>
        Assert.Null(PickedFolder.OnDisk(picked));

    [Theory]
    [InlineData(@"C:\Users\testuser\Games", @"C:\Users\testuser\Games")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"\\server.test\share\src", @"\\server.test\share\src")]
    [InlineData(@"C:\Users\testuser\Games\", @"C:\Users\testuser\Games")]
    public void AFolderOnADiskIsThatFolder(string picked, string expected) =>
        Assert.Equal(expected, PickedFolder.OnDisk(picked));
}
