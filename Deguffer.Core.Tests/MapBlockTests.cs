using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shapes on the map that stand for no node: each found from the node number a layout gives it,
/// each named one way wherever it is read, and none of them painted as a thing on the disk is.
/// </summary>
public sealed class MapBlockTests
{
    private static readonly MapBlock[] Blocks =
        [MapBlock.Aggregate, MapBlock.FreeSpace, MapBlock.Unaccounted, MapBlock.ShadowCopies, MapBlock.ReservedStorage];

    /// <summary>The node number a layout gives each block's shape finds that block, and a node of the tree finds none.</summary>
    [Fact]
    public void EachBlockIsFoundFromTheNodeNumberOfItsShapes()
    {
        Assert.Equal(
            [ExploreTile.Aggregated, ExploreTile.FreeSpace, ExploreTile.Unaccounted, ExploreTile.ShadowCopies, ExploreTile.ReservedStorage],
            Blocks.Select(block => block.Node));

        foreach (var block in Blocks)
        {
            Assert.Same(block, MapBlock.Of(block.Node));
            Assert.Same(block, new ExploreTile(block.Node, 0, 1, 0, 0, 1, 1).Block);
            Assert.Same(block, new ExploreHit(block.Node, 1).Block);
        }

        Assert.Null(MapBlock.Of(0));
        Assert.Null(new ExploreHit(3, 1).Block);
        Assert.Null(new ExploreTile(0, 0, 1, 0, 0, 1, 1).Block);
    }

    /// <summary>
    /// The line under the map says the name the map and a screen reader say, and adds to it rather
    /// than wording it another way, so the readers of one shape are told one name.
    /// </summary>
    [Fact]
    public void TheLineUnderTheMapStartsWithTheNameTheMapSays()
    {
        foreach (var block in Blocks)
        {
            Assert.NotEmpty(block.Name);
            Assert.StartsWith(block.Name, block.Readout, StringComparison.Ordinal);
        }
    }

    /// <summary>Every block says that nothing on the map acts on it (§7.1).</summary>
    [Fact]
    public void EveryBlockSaysItCannotBeActedOn()
    {
        Assert.Contains("one by one", MapBlock.Aggregate.Help);

        foreach (var block in Blocks.Where(block => block != MapBlock.Aggregate))
        {
            Assert.Contains("cannot be opened or removed", block.Help);
        }
    }

    /// <summary>Every block is a grey of its own, so none reads as a thing on the disk or as another block.</summary>
    [Fact]
    public void EveryBlockIsAGreyOfItsOwn()
    {
        foreach (var block in Blocks)
        {
            Assert.True(block.Colour.Red == block.Colour.Green && block.Colour.Green == block.Colour.Blue, $"{block} is not a grey.");
        }

        Assert.Equal(Blocks.Length, Blocks.Select(block => block.Colour).Append(TilePalette.FolderFrame).Distinct().Count() - 1);
    }
}
