using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a node of a scanned drive is worded wherever the page names it: what kind of thing it is, its
/// size and age marked where they are bounds, and what its space on disk leaves out.
/// </summary>
public sealed class ExploreNodeTextTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFolderAndAFileAreNamedAsAScreenReaderSaysThem()
    {
        var tree = Drive(Folder("kept"), File("loose.bin", 10));

        Assert.Equal("Folder", ExploreNodeText.Kind(tree, Child(tree, "kept")));
        Assert.Equal("File", ExploreNodeText.Kind(tree, Child(tree, "loose.bin")));
    }

    /// <summary>A folder the walk was refused totals only what it saw, so its size and age are bounds and say so.</summary>
    [Fact]
    public void AFolderWithUnknownSizeBelowIsMarkedAsABound()
    {
        var written = Now.AddDays(-30);
        var tree = Drive(
            new ExploreChild("refused", IsDirectory: true, IsLink: false, Size: 4_000, LastWritten: ExploreTimestamp.FromUtc(written), SizeUnknown: true),
            new ExploreChild("plain.bin", IsDirectory: false, IsLink: false, Size: 4_000, LastWritten: ExploreTimestamp.FromUtc(written)));
        var refused = Child(tree, "refused");
        var plain = Child(tree, "plain.bin");

        Assert.Equal("≥ " + FreeSpace.Format(4_000), ExploreNodeText.Size(tree, refused));
        Assert.Equal(FreeSpace.Format(4_000), ExploreNodeText.Size(tree, plain));
        Assert.Equal($"{RelativeAge.Describe(written, Now)} or newer", ExploreNodeText.Age(tree, refused, Now));
        Assert.Equal(RelativeAge.Describe(written, Now), ExploreNodeText.Age(tree, plain, Now));
    }

    /// <summary>A total with one bound in it is a bound too, and one of measurements alone is not.</summary>
    [Fact]
    public void ATotalWithABoundInItIsMarkedAsABound()
    {
        var tree = Drive(
            new ExploreChild("refused", IsDirectory: true, IsLink: false, Size: 4_000, SizeUnknown: true),
            File("plain.bin", 3_000),
            File("other.bin", 2_000));
        var refused = Child(tree, "refused");
        var plain = Child(tree, "plain.bin");
        var other = Child(tree, "other.bin");

        Assert.Equal("≥ " + FreeSpace.Format(7_000), ExploreNodeText.Total(tree, [plain, refused]));
        Assert.Equal(FreeSpace.Format(5_000), ExploreNodeText.Total(tree, [plain, other]));
    }

    [Fact]
    public void AnAgeNotKnownIsNotQualified()
    {
        var tree = Drive(new ExploreChild("refused", IsDirectory: true, IsLink: false, Size: 0, SizeUnknown: true));

        Assert.Equal(RelativeAge.Describe(null, Now), ExploreNodeText.Age(tree, Child(tree, "refused"), Now));
    }

    [Fact]
    public void TheSpaceOnDiskSaysItsLengthAndWhyWhereTheyDiffer()
    {
        var tree = Drive(
            Stored("cloud.bin", 0, 5_000_000, FileStorage.CloudOnly),
            Stored("packed.bin", 1_000_000, 5_000_000, FileStorage.Compressed | FileStorage.Sparse),
            Stored("three.bin", 0, 5_000_000, FileStorage.CloudOnly | FileStorage.Compressed | FileStorage.Sparse),
            Stored("same.bin", 4_096, 4_000, FileStorage.Plain));

        Assert.Equal($"{FreeSpace.Format(5_000_000)} long, online-only", ExploreNodeText.Storage(tree, Child(tree, "cloud.bin")));
        Assert.Equal($"{FreeSpace.Format(5_000_000)} long, compressed and sparse", ExploreNodeText.Storage(tree, Child(tree, "packed.bin")));
        Assert.Equal(
            $"{FreeSpace.Format(5_000_000)} long, online-only, compressed and sparse",
            ExploreNodeText.Storage(tree, Child(tree, "three.bin")));

        // Rounded up to whole clusters, a length that reads the same says nothing.
        Assert.Equal(string.Empty, ExploreNodeText.Storage(tree, Child(tree, "same.bin")));
    }

    [Fact]
    public void AFolderHoldsSuchFilesRatherThanBeingOne()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Synced")]);
        builder.AddChildren(folder, [Stored("cloud.bin", 0, 0, FileStorage.CloudOnly)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal("holds online-only files", ExploreNodeText.Storage(tree, folder));
    }

    private static ExploreTree Drive(params ExploreChild[] children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, children);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child named {name}.");
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, Size: size);

    private static ExploreChild Stored(string name, long size, long length, FileStorage storage) =>
        new(name, IsDirectory: false, IsLink: false, Size: size, Length: length, Storage: storage);
}
