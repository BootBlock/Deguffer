using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The look on the disk taken immediately before a removal that cannot leave a store behind: a tool's
/// own command, Windows emptying a bin, and Explore moving a folder to the Recycle Bin. It has to
/// find a store at any depth, name it in the form a plan compares, and never follow a link into a
/// tree nobody asked about.
/// </summary>
public sealed class MailStoreSearchTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void FindsAStoreAtAnyDepthAndNamesItInDisplayForm()
    {
        var root = _temp.CreateDirectory("cache");
        var shallow = _temp.CreateFile(16, "cache", "someone@example.com.OST");
        var deep = _temp.CreateFile(16, "cache", "a", "b", "c", "archive.pst");
        _temp.CreateFile(16, "cache", "a", "archive.pst.txt");
        _temp.CreateFile(16, "cache", "a", "b", "blob.bin");

        var found = MailStoreSearch.Under(root, WindowsFileSystem.Default, default);

        Assert.Equal([deep, shallow], found.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(found, path => Assert.DoesNotContain(@"\\?\", path, StringComparison.Ordinal));
    }

    [Fact]
    public void FindsNothingWhereThereIsNothing()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "blob.bin");

        Assert.Empty(MailStoreSearch.Under(root, WindowsFileSystem.Default, default));
        Assert.Empty(MailStoreSearch.Under(Path.Combine(_temp.Path, "never-existed"), WindowsFileSystem.Default, default));
    }

    /// <summary>
    /// A link is never followed. What is on the far side belongs to wherever it points, and a removal
    /// here removes the link and nothing behind it — so a store behind a link is not this removal's
    /// to answer for, and reading it would walk a tree nobody named.
    /// </summary>
    [Fact]
    public void NeverFollowsALink()
    {
        var root = _temp.CreateDirectory("cache");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(16, "elsewhere", "archive.pst");

        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), elsewhere);

        Assert.Empty(MailStoreSearch.Under(root, WindowsFileSystem.Default, default));
    }

    /// <summary>§6.3: every path it hands the filesystem is in extended-length form.</summary>
    [Fact]
    public void HandsTheFilesystemExtendedPaths()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "nested", "blob.bin");

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);

        MailStoreSearch.Under(root, recorder, default);

        Assert.NotEmpty(recorder.Paths);
        Assert.All(recorder.Paths, p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
    }
}
