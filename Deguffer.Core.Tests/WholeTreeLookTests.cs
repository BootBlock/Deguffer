using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The look on the disk taken immediately before a removal that cannot leave a store behind: a tool's
/// own command, Windows emptying a bin, a folder removed whole, and Explore moving a folder to the
/// Recycle Bin. It has to find a store at any depth, name it in the form a plan compares, report a
/// folder it could not list rather than read it as holding nothing, and never follow a link into a
/// tree nobody asked about.
/// </summary>
public sealed class WholeTreeLookTests : IDisposable
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

        var found = Stores(root);

        Assert.Equal([deep, shallow], found.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(found, path => Assert.DoesNotContain(@"\\?\", path, StringComparison.Ordinal));
    }

    [Fact]
    public void FindsNothingWhereThereIsNothing()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "blob.bin");

        Assert.Empty(Stores(root));
        Assert.Empty(Stores(Path.Combine(_temp.Path, "never-existed")));
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

        SymbolicLink.ToDirectory(Path.Combine(root, "linked"), elsewhere);

        Assert.Empty(Stores(root));
    }

    /// <summary>
    /// A folder link is never followed, but a file carrying a link's mark and named like a store is
    /// found: a OneDrive placeholder and a deduplicated file carry that mark too, and the removal this
    /// look stands in front of would delete their content.
    /// </summary>
    [Fact]
    public void FindsAFileNamedLikeAStoreThatCarriesTheMarkOfALink()
    {
        var root = _temp.CreateDirectory("cache");
        var target = _temp.CreateFile(16, "elsewhere", "archive.pst");
        var link = Path.Combine(root, "shortcut.pst");

        SymbolicLink.ToFile(link, target);

        Assert.Equal([link], Stores(root));
    }

    /// <summary>§6.3: every path it hands the filesystem is in extended-length form.</summary>
    [Fact]
    public void HandsTheFilesystemExtendedPaths()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "nested", "blob.bin");

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);

        WholeTreeLook.Take([root], MinimumAge.Off, recorder, default);

        Assert.NotEmpty(recorder.Paths);
        Assert.All(recorder.Paths, p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
    }

    /// <summary>
    /// §9 against §5.3. A folder Windows will not list is skipped by a removal Deguffer performs, but a
    /// removal that goes whole takes it unlisted, so the look names it apart from the stores it found
    /// rather than answering "no stores" for a part it never saw.
    /// </summary>
    [Fact]
    public void NamesAFolderItCouldNotListApartFromTheStoresItFound()
    {
        var root = _temp.CreateDirectory("cache");
        var locked = _temp.CreateDirectory("cache", "a", "locked");
        _temp.CreateFile(16, "cache", "a", "locked", "archive.pst");
        var found = _temp.CreateFile(16, "cache", "b", "mail.ost");

        var look = WholeTreeLook.Take(
            [root], MinimumAge.Off, new UnlistableFileSystem(WindowsFileSystem.Default, locked), default);

        Assert.Equal([found], look.Stores);
        Assert.Equal([locked], look.Unlisted);
    }

    /// <summary>
    /// The same refusal from Windows itself rather than a fake, on the folder asked about: nothing
    /// below it was seen, so it cannot be answered for.
    /// </summary>
    [Fact]
    public void NamesTheFolderAskedAboutWhenWindowsWillNotListIt()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(16, "cache", "archive.pst");

        using var denied = new DeniedDirectory(root);

        var look = WholeTreeLook.Take([root], MinimumAge.Off, WindowsFileSystem.Default, default);

        Assert.Empty(look.Stores);
        Assert.Equal([root], look.Unlisted);
        Assert.StartsWith(
            $"Not run: Windows would not let Deguffer look inside {root}, so it cannot tell whether an Outlook data file is there.",
            look.WhyNot("Not run", "It goes whole", MinimumAge.Off),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A command's measured paths can nest, and a folder walked twice would name its store twice, so
    /// a run would say it held back two files where there is one.
    /// </summary>
    [Fact]
    public void NamesAStoreOnceWhenThePathsAskedAboutNest()
    {
        var root = _temp.CreateDirectory("cache");
        var nested = _temp.CreateDirectory("cache", "a");
        var store = _temp.CreateFile(16, "cache", "a", "archive.pst");

        var look = WholeTreeLook.Take([root, nested], MinimumAge.Off, WindowsFileSystem.Default, default);

        Assert.Equal([store], look.Stores);
    }

    private static IReadOnlyList<string> Stores(string root) =>
        WholeTreeLook.Take([root], MinimumAge.Off, WindowsFileSystem.Default, default).Stores;
}
