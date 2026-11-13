using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk for a caller that builds each child's path itself and keeps it: the walk lists the child by
/// that same string, so the caller holds one copy rather than two, and lists nothing but the entry it
/// was handed with, so a mistaken path cannot take the walk into a tree nobody classified.
/// </summary>
public sealed class WalkByPathTests
{
    private const string Root = @"\\?\Q:\cache";

    private static readonly WalkTuning OneWorker = new(1, WalkTuning.MinimumListingBuffer);

    [Fact]
    public void ListsEachChildByTheStringTheCallerHandedOver()
    {
        var handed = new List<string>();
        var listed = new List<string>();

        Walk(
            Lister(listed, ("child", FileAttributes.Directory)),
            (contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    var path = entry.FullName;
                    handed.Add(path);
                    descend(entry, path, 0);
                }
            });

        var child = Assert.Single(handed);
        Assert.Equal(@"\\?\Q:\cache\child", child);
        Assert.Same(child, listed[1]);
    }

    /// <summary>
    /// A path that is not the entry's own is refused rather than listed: the caller classified the
    /// entry, and listing anything else would walk a tree it never saw.
    /// </summary>
    [Theory]
    [InlineData(@"\\?\Q:\cache\other")]
    [InlineData(@"\\?\Q:\cache\CHILD")]
    [InlineData(@"\\?\Q:\cache\child\")]
    [InlineData(@"\\?\Q:\child")]
    public void RefusesAPathThatIsNotTheEntrysOwn(string wrong)
    {
        var listed = new List<string>();

        var thrown = Assert.Throws<ArgumentException>(() => Walk(
            Lister(listed, ("child", FileAttributes.Directory)),
            (contents, descend) =>
            {
                foreach (var entry in contents.Entries)
                {
                    descend(entry, wrong, 0);
                }
            }));

        Assert.Equal("path", thrown.ParamName);
        Assert.Equal([Root], listed);
    }

    /// <summary>The walk holds the rule that a link is never followed, whichever way it is descended.</summary>
    [Fact]
    public void NeverDescendsIntoALinkHandedWithItsOwnPath()
    {
        var listed = new List<string>();

        Walk(
            Lister(listed, ("junction", FileAttributes.Directory | FileAttributes.ReparsePoint)),
            (contents, descend) =>
            {
                foreach (var entry in contents.Links)
                {
                    descend(entry, entry.FullName, 0);
                }
            });

        Assert.Equal([Root], listed);
    }

    [Theory]
    [InlineData(@"\\?\Q:\cache", "a.bin", @"\\?\Q:\cache\a.bin", true)]
    [InlineData(@"\\?\Q:\", "a.bin", @"\\?\Q:\a.bin", true)]
    [InlineData(@"\\?\Q:\", "a.bin", @"\\?\Q:\\a.bin", false)]
    [InlineData(@"\\?\Q:\cache", "a.bin", @"\\?\Q:\cacheXa.bin", false)]
    [InlineData(@"\\?\Q:\cache", "a.bin", @"\\?\Q:\cache\A.bin", false)]
    [InlineData(@"\\?\Q:\cache", "a.bin", @"\\?\Q:\cache\a.bin2", false)]
    public void KnowsItsOwnFullName(string directory, string name, string path, bool expected)
    {
        var entry = new WalkEntry(directory, name, FileAttributes.Archive, 0, 0, 0);

        Assert.Equal(expected, entry.IsFullName(path));
        Assert.Equal(expected, entry.FullName == path);
    }

    private static void Walk(ListDirectory list, Action<DirectoryContents, Action<WalkEntry, string, int>> atRoot) =>
        BoundedFileWalk.VisitByPath(
            Root,
            0,
            OneWorker,
            FileNames.All,
            list,
            (depth, contents, descend) =>
            {
                if (depth == 0)
                {
                    atRoot(contents, (entry, path, _) => descend(entry, path, 1));
                }
            },
            static _ => { },
            TimeProvider.System,
            default);

    /// <summary>The root holds <paramref name="children"/>, and every folder below it is empty.</summary>
    private static ListDirectory Lister(List<string> listed, params (string Name, FileAttributes Attributes)[] children) =>
        (directory, request, into) =>
        {
            listed.Add(directory);

            if (directory == Root)
            {
                foreach (var (name, attributes) in children)
                {
                    into.Add(new WalkEntry(directory, request.NameOf(name, isDirectory: true), attributes, 0, 0, 0));
                }
            }

            return ListingEnd.Listed;
        };
}
