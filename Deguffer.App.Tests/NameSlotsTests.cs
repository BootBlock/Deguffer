using Deguffer.App.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// The map's names are made once and kept, and a node named again within one tree keeps the control
/// that named it, so its text and its measurement are kept with it.
///
/// <para>With controls of the test's own, because a test host cannot make the window's elements: each
/// counts as one name control made, as the map's <c>Label</c> is.</para>
/// </summary>
public sealed class NameSlotsTests
{
    private static readonly Func<int, int> Itself = static node => node;

    [Fact]
    public void ADrawingWithFewerNamesPutsTheRestAwayAndTheNextWithMoreTakesThemBack()
    {
        var made = 0;
        var slots = new NameSlots<Slot>(() =>
        {
            made++;
            return new Slot();
        });

        slots.Assign(Nodes(0, 10), Itself, sameTree: true);
        var all = slots.All.ToList();

        slots.Assign(Nodes(0, 3), Itself, sameTree: true);
        var putAway = slots.Surplus.Count;

        slots.Assign(Nodes(0, 10), Itself, sameTree: true);

        Assert.Equal(10, made);
        Assert.Equal(7, putAway);
        Assert.Equal(all, slots.All);
        Assert.Empty(slots.Surplus);
    }

    /// <summary>
    /// A zoom names the same nodes in another order, and some new ones in the room the others left.
    /// Handed out by position, no name of the first drawing would keep its control.
    /// </summary>
    [Fact]
    public void ANodeNamedAgainKeepsItsControl()
    {
        var made = 0;
        var slots = new NameSlots<Slot>(() =>
        {
            made++;
            return new Slot();
        });

        slots.Assign([10, 11, 12, 13], Itself, sameTree: true);
        var named = Enumerable.Range(0, 4).ToDictionary(i => 10 + i, i => slots.Assigned[i]);

        slots.Assign([20, 13, 21, 11, 10], Itself, sameTree: true);

        Assert.Same(named[13], slots.Assigned[1]);
        Assert.Same(named[11], slots.Assigned[3]);
        Assert.Same(named[10], slots.Assigned[4]);
        Assert.Equal(
            [NameHistory.Unnamed, NameHistory.NamedHere, NameHistory.Unnamed, NameHistory.NamedHere, NameHistory.NamedHere],
            slots.Histories);

        // The new names take the control the node no longer named had, then a new one.
        Assert.Same(named[12], slots.Assigned[0]);
        Assert.Equal(5, made);
        Assert.Equal(5, slots.Assigned.Distinct().Count());
    }

    /// <summary>Another tree's nodes are not the same nodes, however their numbers match.</summary>
    [Fact]
    public void AnotherTreesNamesAreHandedOutInOrder()
    {
        var slots = new NameSlots<Slot>(() => new Slot());

        slots.Assign([1, 2, 3], Itself, sameTree: true);
        var first = slots.Assigned.ToList();

        slots.Assign([3, 2, 1], Itself, sameTree: false);

        Assert.Equal(first, slots.Assigned);
        Assert.All(slots.Histories, history => Assert.Equal(NameHistory.Unnamed, history));
    }

    [Fact]
    public void ANodeNamedTwiceKeepsItsControlForTheFirstName()
    {
        var slots = new NameSlots<Slot>(() => new Slot());

        slots.Assign([1, 2], Itself, sameTree: true);
        var one = slots.Assigned[0];

        slots.Assign([1, 1], Itself, sameTree: true);

        Assert.Same(one, slots.Assigned[0]);
        Assert.NotSame(one, slots.Assigned[1]);
        Assert.Equal([NameHistory.NamedHere, NameHistory.NamedElsewhere], slots.Histories);
    }

    [Fact]
    public void ClearedSlotsMakeTheirControlsAgainAndRememberNoNode()
    {
        var made = 0;
        var slots = new NameSlots<Slot>(() =>
        {
            made++;
            return new Slot();
        });

        slots.Assign([1, 2], Itself, sameTree: true);
        slots.Clear();
        slots.Assign([1, 2], Itself, sameTree: true);

        Assert.Equal(4, made);
        Assert.All(slots.Histories, history => Assert.Equal(NameHistory.Unnamed, history));
    }

    private static int[] Nodes(int first, int count) => [.. Enumerable.Range(first, count)];

    /// <summary>One name control, known only by which it is.</summary>
    private sealed class Slot;
}
