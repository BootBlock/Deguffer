using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a row's own listings saw while it planned, which §5.6's record of a protected path is taken
/// from: only presence, only for what a listing returned, and what a folder held only where its top
/// level decides it as <see cref="DirectoryContent.IsPresent"/> would.
/// </summary>
public sealed class ListedEntriesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A listing answers for a path given in either form, in any case, and with or without a trailing
    /// separator, and for nothing it did not return.
    /// </summary>
    [Fact]
    public void AListingAnswersForAPathHoweverItIsSpelled()
    {
        var folder = _temp.CreateDirectory("Spelled");
        var child = _temp.CreateDirectory("Spelled", "Child");

        var listed = new ListedEntries();
        listed.AddFolder(LongPath.Extended(folder), FolderEntries.Of(LongPath.Extended(folder))!);

        Assert.True(listed.TryGet(folder + Path.DirectorySeparatorChar, out var folderContent));
        Assert.True(listed.TryGet(child.ToUpperInvariant(), out var childContent));
        Assert.False(listed.TryGet(Path.Join(folder, "Other"), out _));

        // The folder held only a folder, and the child was returned without being listed.
        Assert.Equal([null, null], [folderContent, childContent]);
    }

    /// <summary>
    /// A listing settles what a folder holds only where its top level decides it: a file or a link is
    /// content and no entry at all is none, which is what the walk answers. A folder holding only
    /// folders, and a link itself, are left to be asked.
    /// </summary>
    [Fact]
    public void AListingSettlesContentOnlyWhereTheTopLevelDecidesIt()
    {
        var withFile = _temp.CreateDirectory("with-file");
        var file = _temp.CreateFile(8, "with-file", "one.bin");
        var withLink = _temp.CreateDirectory("with-link");
        var link = Path.Join(withLink, "link");
        Junction.ToDirectory(link, withFile);
        var empty = _temp.CreateDirectory("empty");
        var folders = _temp.CreateDirectory("folders");
        _temp.CreateDirectory("folders", "inner");

        var listed = new ListedEntries();

        foreach (var folder in new[] { withFile, withLink, empty, folders })
        {
            listed.AddFolder(folder, FolderEntries.Of(folder)!);
        }

        string[] paths = [withFile, withLink, empty, file, folders, link];

        Assert.Equal(
            [true, true, false, false, null, null],
            paths.Select(path => listed.TryGet(path, out var held) ? held : throw new InvalidOperationException($"Not listed: {path}")));

        // Where the listing settled it, the walk agrees.
        Assert.Equal([true, true, false, false], paths.Take(4).Select(DirectoryContent.IsPresent));
    }

    /// <summary>Two listings of one path keep the one that settled what it holds, in either order.</summary>
    [Fact]
    public void ASettledAnswerIsKeptOverAnUnsettledOne()
    {
        var parent = _temp.CreateDirectory("parent");
        var child = _temp.CreateDirectory("parent", "child");
        _temp.CreateFile(8, "parent", "child", "one.bin");

        var fromParent = new ListedEntries();
        fromParent.AddFolder(parent, FolderEntries.Of(parent)!);

        var fromChild = new ListedEntries();
        fromChild.AddFolder(child, FolderEntries.Of(child)!);

        var parentFirst = new ListedEntries();
        parentFirst.Include(fromParent);
        parentFirst.Include(fromChild);

        var childFirst = new ListedEntries();
        childFirst.Include(fromChild);
        childFirst.Include(fromParent);

        Assert.True(parentFirst.TryGet(child, out var one));
        Assert.True(childFirst.TryGet(child, out var other));
        Assert.Equal([true, true], [one, other]);
    }
}
