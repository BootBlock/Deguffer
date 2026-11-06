using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each of a known set of files is mounted is asked once for the folder holding it, and every
/// other path is asked about at the call (#539).
/// </summary>
public sealed class FileMountPointsTests
{
    private const string Folder = @"C:\Users\testuser\Documents";
    private const string Mounted = @"C:\Users\testuser\Documents\Mount";

    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\");

    /// <summary>
    /// Every file named in one folder is answered as the first, in whichever form it arrives: a path in
    /// the extended form §6.3 requires names the same file as its display form.
    /// </summary>
    [Fact]
    public void FilesInOneFolderAreAskedAboutOnce()
    {
        var files = new FileMountPoints(_volumes, [$@"{Folder}\a.jpg", $@"{Folder}\b.jpg", $@"{Folder}\c.jpg"]);

        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\a.jpg"));
        Assert.Equal(@"C:\", files.MountPointOf($@"\\?\{Folder}\b.jpg"));
        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\C.JPG"));

        Assert.Equal([$@"{Folder}\a.jpg"], _volumes.MountPointQueries);
    }

    /// <summary>
    /// A folder is never answered by its parent's answer, because a volume can be mounted at the
    /// folder itself, and read as below its parent's volume it would be a thing inside a folder rather
    /// than a whole volume. So is a file it was not given, which may be one a link reaches.
    /// </summary>
    [Fact]
    public void APathItWasNotGivenIsAskedAboutAtTheCall()
    {
        _volumes.With(Mounted + Path.DirectorySeparatorChar);
        var files = new FileMountPoints(_volumes, [$@"{Folder}\a.jpg"]);

        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\a.jpg"));
        Assert.Equal(Mounted + Path.DirectorySeparatorChar, files.MountPointOf(Mounted));
        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\other.jpg"));
        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\other.jpg"));

        Assert.Equal(
            [$@"{Folder}\a.jpg", Mounted, $@"{Folder}\other.jpg", $@"{Folder}\other.jpg"],
            _volumes.MountPointQueries);
    }

    /// <summary>
    /// Each folder is asked about on its own, so a volume mounted at one folder after another folder was
    /// answered is seen by the files in it.
    /// </summary>
    [Fact]
    public void EachFolderIsAskedAboutOnItsOwn()
    {
        var files = new FileMountPoints(_volumes, [$@"{Folder}\a.jpg", $@"{Mounted}\pagefile.sys"]);

        Assert.Equal(@"C:\", files.MountPointOf($@"{Folder}\a.jpg"));

        _volumes.With(Mounted + Path.DirectorySeparatorChar);

        Assert.Equal(Mounted + Path.DirectorySeparatorChar, files.MountPointOf($@"{Mounted}\pagefile.sys"));
    }

    /// <summary>Invalidating forgets every folder's answer, as the caller asked for what was mounted since to be seen.</summary>
    [Fact]
    public void InvalidatingForgetsEveryAnswer()
    {
        var files = new FileMountPoints(_volumes, [$@"{Folder}\a.jpg"]);

        files.MountPointOf($@"{Folder}\a.jpg");
        files.Invalidate();
        files.MountPointOf($@"{Folder}\a.jpg");

        Assert.Equal(2, _volumes.MountPointQueries.Count);
        Assert.Equal(1, _volumes.InvalidateCount);
    }
}
