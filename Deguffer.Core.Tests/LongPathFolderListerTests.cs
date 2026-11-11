using Deguffer.Core.Exploring.Network;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the network browser and the Path… box list one level of a folder: names of folders alone,
/// hidden and system folders left out, asked in the extended form (§6.3), and a refusal told as one.
/// </summary>
public sealed class LongPathFolderListerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AFolderListsTheFoldersInsideItByName()
    {
        _temp.CreateDirectory("share", "Photos");
        _temp.CreateDirectory("share", "Films");
        _temp.CreateFile(5, "share", "notes.txt");
        var hidden = _temp.CreateDirectory("share", "$RECYCLE.BIN");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);

        var listed = new LongPathFolderLister(WindowsFileSystem.Default).Folders(Path.Combine(_temp.Path, "share"), CancellationToken.None);

        Assert.True(listed.Succeeded);
        Assert.Equal(["Films", "Photos"], listed.Entries.Order(StringComparer.Ordinal));
    }

    /// <summary>The folder is asked in the extended form, so a path past MAX_PATH is never cut short (§6.3).</summary>
    [Fact]
    public void TheFolderIsAskedInTheExtendedForm()
    {
        var folder = _temp.CreateDirectory("share");
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);

        new LongPathFolderLister(recording).Folders(folder, CancellationToken.None);

        Assert.Equal([LongPath.Extended(folder)], recording.Listed);
        Assert.StartsWith(@"\\?\", Assert.Single(recording.Listed), StringComparison.Ordinal);
    }

    /// <summary>A folder that is not there is not found, rather than a folder with nothing in it.</summary>
    [Fact]
    public void AFolderThatIsNotThereIsNotFound()
    {
        var listed = new LongPathFolderLister(WindowsFileSystem.Default).Folders(Path.Combine(_temp.Path, "gone"), CancellationToken.None);

        Assert.Equal(NetworkFailure.NotFound, listed.Failure);
    }

    /// <summary>
    /// A folder this account may not list offers a sign-in, and a listing refused part-way offers
    /// none of what it read: half a folder is not shown as the whole of it (§5.3).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFolderThisAccountMayNotListAsksForASignIn(bool partWay)
    {
        var folder = _temp.CreateDirectory("share");
        _temp.CreateDirectory("share", "Photos");

        var listed = new LongPathFolderLister(new UnlistableFileSystem(WindowsFileSystem.Default, folder, partWay))
            .Folders(folder, CancellationToken.None);

        Assert.Equal(NetworkFailure.NeedsSignIn, listed.Failure);
        Assert.Empty(listed.Entries);
    }
}
