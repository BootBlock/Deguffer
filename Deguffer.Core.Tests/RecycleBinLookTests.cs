using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The look an Explore folder gets before the shell moves it to the Recycle Bin (§9): a store inside
/// a folder stops the move, a link of either kind is moved as a link, and a folder Windows will not
/// describe stays where it is.
/// </summary>
public sealed class RecycleBinLookTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ExploreItem Folder(string path) => new(path, IsDirectory: true, Bytes: 0, LowerBound: false);

    [Fact]
    public async Task KeepsAFolderThatHoldsAnOutlookDataFile()
    {
        var folder = _temp.CreateDirectory("mail");
        var store = _temp.CreateFile(16, "mail", "archive.pst");

        var why = await RecycleBinLook.WhyItStaysAsync(Folder(folder), WindowsFileSystem.Default, default);

        Assert.StartsWith($"'mail' holds an Outlook data file, at {store}.", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder link is moved as a link and takes nothing on its far side, so a store there does not
    /// stop it, and the link is described once.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task MovesAFolderLinkWithoutLookingThroughIt(DirectoryLinkKind kind)
    {
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(16, "elsewhere", "archive.pst");
        var link = Path.Combine(_temp.Path, "mail");
        DirectoryLink.Create(kind, link, elsewhere);

        var fs = new RecordingFileSystem(WindowsFileSystem.Default);

        Assert.Null(await RecycleBinLook.WhyItStaysAsync(Folder(link), fs, default));
        Assert.Equal(LongPath.Extended(link), Assert.Single(fs.Described));
        Assert.Empty(fs.Listed);
    }

    /// <summary>
    /// A file symbolic link where the folder was is a link too, and is moved as one. The directory
    /// probe calls it absent, so an answer taken from that probe would send it to the look, which
    /// names it as a file where the folder was.
    /// </summary>
    [Fact]
    public async Task MovesAFileLinkWhereTheFolderWasAsALink()
    {
        var target = _temp.CreateFile(16, "elsewhere", "archive.pst");
        var link = Path.Combine(_temp.Path, "mail");
        SymbolicLink.ToFile(link, target);

        var fs = new RecordingFileSystem(WindowsFileSystem.Default);

        Assert.Null(await RecycleBinLook.WhyItStaysAsync(Folder(link), fs, default));
        Assert.Equal(LongPath.Extended(link), Assert.Single(fs.Described));
    }

    /// <summary>A plain file where the folder was is not a link, so the look meets it and says so.</summary>
    [Fact]
    public async Task NamesAFileWhereTheFolderWas()
    {
        var file = _temp.CreateFile(16, "mail");

        var why = await RecycleBinLook.WhyItStaysAsync(Folder(file), WindowsFileSystem.Default, default);

        Assert.Equal(
            $"Not moved to the Recycle Bin: a file now stands where the folder {file} was, and Deguffer left it alone.",
            why);
    }

    /// <summary>
    /// A folder Windows will not describe is neither a link nor a folder, so it is not moved unlooked
    /// at.
    /// </summary>
    [Fact]
    public async Task KeepsAFolderWindowsWouldNotDescribe()
    {
        var folder = _temp.CreateDirectory("mail");
        var fs = new UndescribableDirectoryFileSystem(WindowsFileSystem.Default, folder);

        var why = await RecycleBinLook.WhyItStaysAsync(Folder(folder), fs, default);

        Assert.StartsWith("Windows would not say what is in 'mail'", why, StringComparison.Ordinal);
        Assert.All(fs.Probed, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }
}
