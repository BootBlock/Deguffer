using Deguffer.Core.Exploring;
using Deguffer.Core.Memory;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a node of a memory picture is called, and what the line under the map and a screen reader are
/// told of it.
/// </summary>
public sealed class MemoryTextTests
{
    private static readonly MemoryNodeKey Parent = new(MemoryPart.Process, 100, 10);

    private static readonly MemoryNodeKey Lone = new(MemoryPart.Process, 300, 30);

    private readonly MemoryTree _tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 300, created: 10)
        .Process(200, 100, "child.exe", 100, created: 20)
        .Process(300, 1, "lone.exe", 50, created: 30)
        .Build());

    [Fact]
    public void TheLineUnderTheMapSaysANodesNameItsFiguresAndWhatPartItIs()
    {
        var node = _tree.Find(Lone)!.Value;
        var readout = MemoryText.Readout(_tree, node);

        // The fixture commits twice what it holds privately: 2 × 50 MiB.
        Assert.Equal("lone.exe", readout.Name);
        Assert.Equal(
            $"{FreeSpace.Format(_tree.SizeOf(node))} in memory, {FreeSpace.Format(100 * MemorySnapshotBuilder.MiB)} committed",
            readout.Figures);
        Assert.NotEqual(string.Empty, readout.Note);
        Assert.Equal(MemoryPartGuide.Describe(_tree, node), readout.Note);
    }

    [Fact]
    public void AScreenReaderIsToldAProgramAndWhetherOpeningItShowsMore()
    {
        var parent = _tree.Find(Parent)!.Value;
        var lone = _tree.Find(Lone)!.Value;

        Assert.Equal(new MapShapeFacts(MemoryText.Name(_tree, parent), "Program", Opens: true), MemoryText.Facts(_tree, parent));
        Assert.Equal(new MapShapeFacts(MemoryText.Name(_tree, lone), "Program", Opens: false), MemoryText.Facts(_tree, lone));
    }

    [Fact]
    public void EachPartOfMemoryIsNamedForWhatItIs()
    {
        Assert.Equal("Programs", MemoryText.Kind(_tree, _tree.Find(MemoryNodeKey.Of(MemoryPart.Applications))!.Value));
        Assert.Equal("Memory", MemoryText.Kind(_tree, _tree.RootNode));
    }
}
