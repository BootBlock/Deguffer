using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
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
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void NeverFollowsALink(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("cache");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(16, "elsewhere", "archive.pst");

        DirectoryLink.Create(kind, Path.Combine(root, "linked"), elsewhere);

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
    /// A file that has taken the name of the folder asked about lists nothing, but Windows refused
    /// nothing either. It is named apart from a folder Windows would not list, which is still named as
    /// one beside it, and the sentence says what is there rather than blaming a refusal.
    /// </summary>
    [Fact]
    public void NamesAFileWhereTheFolderAskedAboutWasApartFromAFolderItCouldNotList()
    {
        var file = _temp.CreateFile(16, "cache");
        var other = _temp.CreateDirectory("other");
        var locked = _temp.CreateDirectory("other", "locked");

        var look = WholeTreeLook.Take(
            [file, other], MinimumAge.Off, new UnlistableFileSystem(WindowsFileSystem.Default, locked), default);

        Assert.Empty(look.Stores);
        Assert.Equal([file], look.NotFolders);
        Assert.Equal([locked], look.Unlisted);

        var alone = WholeTreeLook.Take([file], MinimumAge.Off, WindowsFileSystem.Default, default);

        Assert.Empty(alone.Unlisted);
        Assert.Equal(
            $"Not run: a file now stands where the folder {file} was, so this is not what the preview showed. It goes whole.",
            alone.WhyNot("Not run", "It goes whole", MinimumAge.Off));
    }

    /// <summary>
    /// A command's measured paths can nest, and a folder walked twice would name its store twice, so
    /// a run would say it held back two files where there is one. The folder inside is listed once,
    /// where the walk of the outer one meets it, whichever order the paths come in.
    /// </summary>
    [Fact]
    public void NamesAStoreOnceAndListsAFolderOnceWhenThePathsAskedAboutNest()
    {
        var root = _temp.CreateDirectory("cache");
        var nested = _temp.CreateDirectory("cache", "a");
        var store = _temp.CreateFile(16, "cache", "a", "archive.pst");
        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);

        var look = WholeTreeLook.Take([nested, root], MinimumAge.Off, recorder, default);

        Assert.Equal([store], look.Stores);
        Assert.Single(recorder.Paths, path => path.Equals(LongPath.Extended(nested), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A path inside another that the walk of the outer one never enters, because it is reached through
    /// a link, is still looked inside: the removal acts on that path too, and follows it to whatever it
    /// points at.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public void LooksInsideANestedPathTheOuterWalkDoesNotEnter(DirectoryLinkKind kind)
    {
        var root = _temp.CreateDirectory("cache");
        var elsewhere = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(16, "elsewhere", "archive.pst");
        var linked = Path.Combine(root, "linked");

        DirectoryLink.Create(kind, linked, elsewhere);

        var look = WholeTreeLook.Take([root, linked], MinimumAge.Off, WindowsFileSystem.Default, default);

        Assert.Equal([Path.Combine(linked, "archive.pst")], look.Stores);
    }

    /// <summary>
    /// §9 in a case-sensitive folder, where <c>Mail</c> and <c>mail</c> are two folders: each is looked
    /// inside and each store named, rather than one taken for the other and its store never seen.
    /// </summary>
    [Fact]
    public void LooksInsideFoldersWhoseNamesDifferOnlyInCase()
    {
        var root = CaseSensitiveFolder.Create(Path.Combine(_temp.Path, "cache"));
        var upper = _temp.CreateFile(16, "cache", "Mail", "archive.pst");
        var lower = _temp.CreateFile(16, "cache", "mail", "backup.pst");

        Assert.Equal([upper, lower], Stores(root));
    }

    /// <summary>
    /// A path inside another, spelled in a case other than the one the disk lists it in, is walked
    /// again on its own, because the walk matches it as spelled. The store below it is one file, and
    /// a run reports how many it held back, so it is named once.
    /// </summary>
    [Fact]
    public void NamesAStoreOnceWhenANestedPathIsSpelledInAnotherCase()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateDirectory("cache", "npm");
        var store = _temp.CreateFile(16, "cache", "npm", "archive.pst");

        var look = WholeTreeLook.Take([root, Path.Combine(root, "NPM")], MinimumAge.Off, WindowsFileSystem.Default, default);

        Assert.Equal(store, Assert.Single(look.Stores), ignoreCase: true);
    }

    /// <summary>
    /// A folder Windows will not list is learned from the listing's answer, never from an exception:
    /// these looks cross entire Windows installations, which hold hundreds of such folders.
    /// </summary>
    [Fact]
    public void NamesAFolderWindowsWillNotListWithoutThrowing()
    {
        var root = _temp.CreateDirectory("cache");
        var locked = _temp.CreateDirectory("cache", "a", "locked");
        var store = _temp.CreateFile(16, "cache", "b", "mail.ost");

        using var denied = new DeniedDirectory(locked);

        // One worker, which is the calling thread: an exception on a helper would not be seen here,
        // because the walk starts its helpers without the context that records what is thrown.
        WholeTreeLook? look = null;
        var thrown = ThrownExceptions.During(
            () => look = WholeTreeLook.Take(
                [root], MinimumAge.Off, WindowsFileSystem.Default, new WalkTuning(1, WalkTuning.MinimumListingBuffer), default));

        Assert.Empty(thrown);
        Assert.Equal([store], look!.Stores);
        Assert.Equal([locked], look.Unlisted);
    }

    /// <summary>
    /// The answer is the same however many workers looked, and names each store and each unlisted
    /// folder once, in one order, when one path asked about lies inside another.
    /// </summary>
    [Fact]
    public void GivesTheSameAnswerWithOneWorkerAsWithMany()
    {
        var root = _temp.CreateDirectory("cache");
        for (var branch = 0; branch < 8; branch++)
        {
            for (var level = 0; level < 4; level++)
            {
                _temp.CreateFile(4, [.. new[] { "cache", $"branch-{branch}" }, .. Enumerable.Range(0, level).Select(l => $"level-{l}"), $"blob-{level}.bin"]);
            }

            _temp.CreateFile(4, "cache", $"branch-{branch}", "level-0", $"mail-{branch}.pst");
        }

        var nested = Path.Combine(root, "branch-3");
        var locked = _temp.CreateDirectory("cache", "branch-5", "locked");

        using var denied = new DeniedDirectory(locked);

        var keep = MinimumAge.Within(TimeSpan.FromDays(1), DateTime.UtcNow);
        var one = WholeTreeLook.Take([nested, root], keep, WindowsFileSystem.Default, new WalkTuning(1, WalkTuning.MinimumListingBuffer), default);

        Assert.Equal(8, one.Stores.Count);
        Assert.Equal([locked], one.Unlisted);
        Assert.True(one.Recent);

        foreach (var tuning in new[]
        {
            new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MinimumListingBuffer),
            new WalkTuning(WalkTuning.MaximumThreads, WalkTuning.MaximumListingBuffer),
        })
        {
            var many = WholeTreeLook.Take([nested, root], keep, WindowsFileSystem.Default, tuning, default);

            Assert.Equal(one.Stores, many.Stores);
            Assert.Equal(one.Unlisted, many.Unlisted);
            Assert.Equal(one.Recent, many.Recent);
        }
    }

    private static IReadOnlyList<string> Stores(string root) =>
        WholeTreeLook.Take([root], MinimumAge.Off, WindowsFileSystem.Default, default).Stores;
}
