using Deguffer.App.ViewModels;
using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// One line of the memory list, which is the picture's contents for a reader who cannot use the
/// picture (§7.2): what each thing is called, how much of its part it is, and whether it opens.
/// </summary>
public sealed class MemoryRowTests
{
    private static readonly MemoryNodeKey Alpha = new(MemoryPart.Process, 100, 10);

    private static MemoryTree Tree() => MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 300, created: 10)
        .Process(200, 100, "beta.exe", 100, created: 20)
        .Build());

    /// <summary>A process's own share is drawn inside it, so it reads as the process itself.</summary>
    [Fact]
    public void AProcesssOwnShareIsCalledTheProcessItself()
    {
        var tree = Tree();
        var own = tree.Find(Alpha with { Part = MemoryPart.OwnShare })!.Value;

        var row = new MemoryRow(tree, own, tree.SizeOf(tree.Find(Alpha)!.Value));

        Assert.Equal("alpha.exe itself", row.Name);
        Assert.False(row.HasChildren);
    }

    /// <summary>Nesting is not visible in a list, so a row that opens says so in words as well as by its chevron.</summary>
    [Fact]
    public void ARowThatHoldsMoreSaysSo()
    {
        var tree = Tree();
        var alpha = tree.Find(Alpha)!.Value;

        var row = new MemoryRow(tree, alpha, tree.SizeOf(tree.RootNode));

        Assert.True(row.HasChildren);
        Assert.EndsWith(", holds more", row.Description, StringComparison.Ordinal);
        Assert.Equal("Show what alpha.exe holds", row.Opens);
    }

    [Fact]
    public void ARowsBarIsItsShareOfThePartItIsIn()
    {
        var tree = Tree();
        var alpha = tree.Find(Alpha)!.Value;
        var beta = tree.Find(new MemoryNodeKey(MemoryPart.Process, 200, 20))!.Value;

        var row = new MemoryRow(tree, beta, tree.SizeOf(alpha));

        Assert.Equal(100.0 * tree.SizeOf(beta) / tree.SizeOf(alpha), row.Share, precision: 6);
    }

    /// <summary>A part that holds nothing draws an empty bar rather than one of NaN width.</summary>
    [Fact]
    public void ARowInAPartThatHoldsNothingHasNoShare()
    {
        var tree = Tree();

        var row = new MemoryRow(tree, tree.Find(Alpha)!.Value, partTotal: 0);

        Assert.Equal(0, row.Share);
    }

    /// <summary>A newer reading rewrites the row it already is, so the list keeps its container.</summary>
    [Fact]
    public void ARowIsBroughtUpToDateWhereItStands()
    {
        var first = Tree();
        var second = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(50, 1, "gamma.exe", 9_000, created: 5)
            .Process(100, 1, "alpha.exe", 600, created: 10)
            .Build());
        var row = new MemoryRow(first, first.Find(Alpha)!.Value, first.SizeOf(first.RootNode));

        row.Describe(second, second.Find(Alpha)!.Value, second.SizeOf(second.RootNode));

        Assert.Equal(second.Find(Alpha), row.Node);
        Assert.Equal(Alpha, row.Key);
        Assert.False(row.HasChildren);
    }
}
