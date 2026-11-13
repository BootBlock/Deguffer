using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What is marked as going once a removal has said what of the selection it will act on: exactly the
/// picks it will act on, never a refused one (§7.1), and found without searching the list per pick.
/// </summary>
public sealed class ExploreGoingTests
{
    private const string Root = @"C:\Users\testuser\Downloads";

    /// <summary>
    /// A pick the policy refused is not marked, and the picks around it are, in the order picked. An
    /// item is matched by its value, as the removal hands back items equal to the ones it was given.
    /// </summary>
    [Fact]
    public void OnlyThePicksTheRemovalWillActOnAreMarked()
    {
        var items = new[] { Item("a"), Item("refused"), Item("b") };

        var going = ExploreGoing.Picks([7, 3, 9], items, [Item("a"), Item("b")]);

        Assert.Equal([7, 9], going);
    }

    /// <summary>A wholly refused selection marks nothing.</summary>
    [Fact]
    public void NothingGoingMarksNothing() =>
        Assert.Empty(ExploreGoing.Picks([7, 3], [Item("a"), Item("b")], []));

    /// <summary>All of the selection going is the selection itself, with nothing built to say so.</summary>
    [Fact]
    public void EverythingGoingIsThePicksThemselves()
    {
        IReadOnlyList<int> picked = [7, 3];

        Assert.Same(picked, ExploreGoing.Picks(picked, [Item("a"), Item("b")], [Item("a"), Item("b")]));
    }

    /// <summary>
    /// Ctrl+A over a folder of twenty thousand, half of it refused, is matched with a comparison or so
    /// per pick. A search of the list per pick makes a hundred million, on the page's thread, before
    /// the dialog can appear.
    /// </summary>
    [Fact]
    public void AWholeFolderIsMatchedWithoutSearchingTheListPerPick()
    {
        const int Count = 20_000;
        var counter = new Counter();
        var items = Enumerable.Range(0, Count).Select(i => new Counted(i, counter)).ToArray();
        var picked = Enumerable.Range(0, Count).Select(i => i + 100).ToArray();
        var going = items.Where(item => item.Key % 2 == 0).ToArray();

        var marked = ExploreGoing.Picks(picked, items, going);

        Assert.Equal(picked.Where((_, i) => i % 2 == 0), marked);
        Assert.InRange(counter.Comparisons, 0, 2 * Count);
    }

    private static ExploreItem Item(string name) => new($@"{Root}\{name}", IsDirectory: true, Bytes: 10, LowerBound: false);

    private sealed class Counter
    {
        public long Comparisons { get; set; }
    }

    /// <summary>An item that counts every equality comparison made of it.</summary>
    private sealed class Counted(int key, Counter counter) : IEquatable<Counted>
    {
        public int Key => key;

        public bool Equals(Counted? other)
        {
            counter.Comparisons++;

            return other is not null && other.Key == key;
        }

        public override bool Equals(object? obj) => Equals(obj as Counted);

        public override int GetHashCode() => key;
    }
}
