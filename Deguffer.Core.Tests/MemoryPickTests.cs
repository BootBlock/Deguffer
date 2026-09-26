using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a pick on the Memory page is about, and when it stops being about anything (§7.2, §7.2.1).
///
/// <para>The pick decides which program §7.2.1's one action is aimed at, so each rule is proven
/// where it could aim that action at something nobody picked: a number carried across a reading that
/// renumbered the nodes, an identifier Windows handed to a later process, and a pick that survived the
/// reader moving somewhere else.</para>
/// </summary>
public sealed class MemoryPickTests
{
    private static readonly MemoryNodeKey Alpha = new(MemoryPart.Process, 100, 10);

    private static MemoryTree First() =>
        MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());

    /// <summary>A larger process listed first, so alpha is numbered differently here than in <see cref="First"/>.</summary>
    private static MemoryTree Renumbered() =>
        MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(50, 1, "beta.exe", 900, created: 5)
            .Process(100, 1, "alpha.exe", 350, created: 10)
            .Build());

    private static MemoryPick PickOf(MemoryTree tree, MemoryNodeKey key) =>
        MemoryPick.Of(tree, tree.Find(key)!.Value)!;

    [Fact]
    public void AProgramPickedIsTheProgramAndTheReadingItWasPickedFrom()
    {
        var tree = First();
        var pick = PickOf(tree, Alpha);

        Assert.Equal(100, pick.Target?.ProcessId);
        Assert.Same(tree.Snapshot, pick.PickedFrom);

        // Windows has to be asked about a program: nothing is settled without it.
        Assert.Null(pick.Settled);
    }

    [Fact]
    public void APartOfThePictureIsSettledAsNotAProgramWithoutAskingAnything()
    {
        var tree = First();
        var pick = PickOf(tree, MemoryNodeKey.Of(MemoryPart.Windows));

        Assert.Null(pick.Target);
        Assert.Same(MemoryTarget.NotAProgram, pick.Settled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void ANumberOutsideTheTreePicksNothing(int node)
    {
        var tree = First();

        Assert.False(tree.Holds(node));
        Assert.Null(MemoryPick.Of(tree, node));
    }

    [Fact]
    public void TheLastNodeOfTheTreeIsANodeOfIt()
    {
        var tree = First();

        Assert.True(tree.Holds(tree.NodeCount - 1));
        Assert.False(tree.Holds(tree.NodeCount));
        Assert.NotNull(MemoryPick.Of(tree, tree.NodeCount - 1));
    }

    [Fact]
    public void NothingOnScreenOrNothingChosenPicksNothing()
    {
        Assert.Null(MemoryPick.Of(null, 0));
        Assert.Null(MemoryPick.Of(First(), null));
    }

    /// <summary>
    /// A reading renumbers every node, so a pick carried by its number would point at whatever sits
    /// there now. The fixture is checked to renumber alpha before the carry is relied on.
    /// </summary>
    [Fact]
    public void AReadingCarriesThePickByWhatItIsAndKeepsTheReadingItWasPickedFrom()
    {
        var first = First();
        var second = Renumbered();
        var pick = PickOf(first, Alpha);

        Assert.NotEqual(pick.Node, second.Find(Alpha));

        var carried = pick.After(MemoryViewChange.Reading, second);

        Assert.NotNull(carried);
        Assert.Equal(second.Find(Alpha), carried.Node);
        Assert.Same(second, carried.Tree);
        Assert.Same(pick.Target, carried.Target);
        Assert.Same(first.Snapshot, carried.PickedFrom);
    }

    /// <summary>
    /// Windows reuses identifiers, and a pick that followed the number onto a later process would
    /// aim the one action at a program nobody picked. It is dropped, never replaced.
    /// </summary>
    [Fact]
    public void AReadingDropsAPickWhoseProcessHasGoneEvenWhereItsIdentifierLivesOn()
    {
        var pick = PickOf(First(), Alpha);
        var reused = MemoryTreeBuilder.Build(
            new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 99).Build());

        Assert.Null(pick.After(MemoryViewChange.Reading, reused));
    }

    /// <summary>
    /// A program picked in one part of the tree is not picked in the next, even where the part the
    /// reader moved to still holds it.
    /// </summary>
    [Fact]
    public void ANavigationDropsThePickEvenWhereItCouldBeFoundAgain()
    {
        var tree = First();
        var pick = PickOf(tree, Alpha);

        Assert.NotNull(pick.After(MemoryViewChange.Reading, tree));
        Assert.Null(pick.After(MemoryViewChange.Navigation, tree));
    }

    [Fact]
    public void OnlyAnAllowedVerdictLetsAPressGoOnToTheProgram()
    {
        var pick = PickOf(First(), Alpha);

        Assert.Same(pick.Target, pick.ToClose(MemoryVerdict.Allow([new ProcessWindow(0x11, "Editor.Window")])));
        Assert.Null(pick.ToClose(MemoryVerdict.Refuse("No.")));
        Assert.Null(pick.ToClose(MemoryActionPolicy.Unanswered));
        Assert.Null(pick.ToClose(null));
    }

    [Fact]
    public void APartOfThePictureIsNeverSomethingAPressGoesOnTo()
    {
        var pick = PickOf(First(), MemoryNodeKey.Of(MemoryPart.Windows));

        Assert.Null(pick.ToClose(MemoryVerdict.Allow([new ProcessWindow(0x11, "Editor.Window")])));
    }
}
