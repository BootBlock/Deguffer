using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the reader can be told about the shape under the pointer, over a treemap that holds a
/// folder the catalogue describes.
///
/// <para>The two halves are covered apart — <see cref="ExploreSurfaceTests"/> for what a point
/// answers with, <see cref="ItemGuideTests"/> for what a path is described as — and the defect was
/// in neither. It was in the join. A folder is drawn as a one-pixel frame round its children, so a
/// point in the middle of it answers with a file nobody wrote about, and a lookup asked about that
/// exact file had nothing to say. The reference was reachable on the frame and nowhere else.</para>
/// </summary>
public sealed class ExploreHoverNoteTests : IDisposable
{
    private const int Canvas = 800;

    /// <summary>Any instant will do while the colouring is by branch, and it must not be the clock.</summary>
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;

    public ExploreHoverNoteTests() => _system = new FakeSystemDirectories(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The middle of the block, which is where a reader points and what the report was about.
    /// </summary>
    [Fact]
    public void TheMiddleOfADescribedFolderIsDescribedByThatFolder()
    {
        var (tree, surface) = Drawn();

        var path = tree.PathOf(surface.At(Canvas / 2f, Canvas / 2f)!.Value.Node);

        // The geometry half: the point answers with something inside the folder, never the folder.
        Assert.NotEqual(_system.WindowsDirectory, path);
        Assert.StartsWith(_system.WindowsDirectory, path, StringComparison.OrdinalIgnoreCase);

        var found = Guide().DescribeNearest(path);

        Assert.Equal("Windows itself", found?.Item.Summary);
        Assert.Equal(_system.WindowsDirectory, found?.Path);
        Assert.False(found?.IsExact);
    }

    /// <summary>
    /// And the frame is the sliver that used to be the only answer, so it still answers — about the
    /// folder itself, with no line saying the explanation came from above.
    /// </summary>
    [Fact]
    public void TheFrameRoundItIsStillTheFolderItself()
    {
        var (tree, surface) = Drawn();

        var found = Guide().DescribeNearest(
            tree.PathOf(surface.At(1.5f, Canvas / 2f)!.Value.Node));

        Assert.Equal(_system.WindowsDirectory, found?.Path);
        Assert.True(found?.IsExact);
    }

    /// <summary>
    /// One described folder filling the canvas, with files inside it. The folder is the tree's only
    /// child so that the middle of the canvas is certainly inside it rather than probably.
    /// </summary>
    private (ExploreTree Tree, ExploreSurface Surface) Drawn()
    {
        var builder = new ExploreTreeBuilder(_temp.Path);

        var windows = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("Windows", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(
            windows,
            [.. Enumerable.Range(0, 4).Select(i =>
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 250_000))]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        return (tree, ExploreSurface.Create(
            tree,
            tree.RootNode,
            ExploreView.Treemap,
            Canvas,
            Canvas,
            scale: 1,
            ExploreColouring.Branch,
            Now));
    }

    /// <summary>
    /// One entry, written here rather than taken from the shipped catalogue. What this asserts is
    /// that an explanation reaches the middle of a block, not what Deguffer says about Windows —
    /// <see cref="KnownItemsTests"/> covers that.
    /// </summary>
    private ItemGuide Guide() => new(
        [new KnownItem(KnownPlace.WindowsDirectory, string.Empty, "Windows itself", "It cannot be deleted.")],
        new Dictionary<KnownPlace, string> { [KnownPlace.WindowsDirectory] = _system.WindowsDirectory });
}
