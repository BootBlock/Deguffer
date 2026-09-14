using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a bound list is told, rather than what it ends up holding. Both matter, but only the first
/// one decides whether the reader sees a list change or sees it rebuilt: a <c>ListView</c> builds a
/// container again for every move, insertion and removal it is told about, and throws away all of
/// them on a reset.
///
/// <para>So a test about what a reading costs counts the changes, and a test about what the reader
/// ends up looking at asserts the result. A pass that arrives at the right list through a change per
/// row is the defect this type exists to remove.</para>
/// </summary>
public sealed class LiveListTests
{
    [Fact]
    public void AListThatHasNotChangedIsToldNothing()
    {
        var rows = Rows("a", "b", "c");
        var watched = Watch(rows);

        Show(rows, "a", "b", "c");

        Assert.Empty(watched);
        Assert.Equal(["a", "b", "c"], Names(rows));
    }

    /// <summary>
    /// The defect this type was written for. Matching the arriving list against the rows by position
    /// alone answers a removal in the middle by sliding the row that has gone down to the end, one
    /// place at a time, which is a move for every row below it. On a list of a hundred processes,
    /// one of them exiting then rebuilt the ninety below it, twice a second.
    /// </summary>
    [Fact]
    public void SomethingThatHasGoneCostsOneRemovalAndNothingElse()
    {
        var rows = Rows("a", "b", "c", "d", "e");
        var watched = Watch(rows);

        Show(rows, "a", "b", "d", "e");

        Assert.Equal([NotifyCollectionChangedAction.Remove], watched);
        Assert.Equal(["a", "b", "d", "e"], Names(rows));
    }

    [Fact]
    public void SomethingNewCostsOneInsertion()
    {
        var rows = Rows("a", "b", "c");
        var watched = Watch(rows);

        Show(rows, "a", "b", "new", "c");

        Assert.Equal([NotifyCollectionChangedAction.Add], watched);
        Assert.Equal(["a", "b", "new", "c"], Names(rows));
    }

    [Fact]
    public void SomethingThatHasChangedPlacesCostsOneMove()
    {
        var rows = Rows("a", "b", "c", "d");
        var watched = Watch(rows);

        Show(rows, "a", "c", "b", "d");

        Assert.Equal([NotifyCollectionChangedAction.Move], watched);
        Assert.Equal(["a", "c", "b", "d"], Names(rows));
    }

    /// <summary>
    /// A row that stays is the same object, which is what the list's container, its selection and
    /// the keyboard's place inside it all hang on.
    /// </summary>
    [Fact]
    public void ARowThatStaysIsTheSameRow()
    {
        var rows = Rows("a", "b", "c");
        var b = rows[1];

        Show(rows, "a", "b", "c");
        Show(rows, "c", "b");

        Assert.Same(b, rows[1]);
    }

    [Fact]
    public void ARowThatStaysIsBroughtUpToDate()
    {
        var rows = Rows("a", "b");

        LiveList.Show(
            rows,
            new[] { ("a", 7), ("b", 9) },
            row => row.Name,
            item => item.Item1,
            item => new Row(item.Item1) { Held = item.Item2 },
            (row, item) => row.Held = item.Item2);

        Assert.Equal([7, 9], rows.Select(row => row.Held));
    }

    /// <summary>
    /// Everything at once, so the passes cannot each be right and wrong together: one gone, one new,
    /// one moved, and the rest untouched.
    /// </summary>
    [Fact]
    public void AReadingThatGainsLosesAndReordersCostsOneChangeForEach()
    {
        var rows = Rows("a", "b", "c", "d", "e");
        var watched = Watch(rows);

        Show(rows, "a", "c", "b", "e", "new");

        Assert.Equal(
            [
                NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Move,
                NotifyCollectionChangedAction.Add,
            ],
            watched);

        Assert.Equal(["a", "c", "b", "e", "new"], Names(rows));
    }

    /// <summary>
    /// A reading with nothing in it empties the list a row at a time rather than resetting it, which
    /// is what a caller that still means the same list wants. A caller showing something else
    /// entirely clears the collection itself, and pays the one reset on purpose.
    /// </summary>
    [Fact]
    public void AnEmptyReadingRemovesEachRowAndDoesNotReset()
    {
        var rows = Rows("a", "b");
        var watched = Watch(rows);

        Show(rows);

        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove], watched);
        Assert.Empty(rows);
    }

    [Fact]
    public void AnArrivingListIsShownInTheOrderItArrived()
    {
        var rows = Rows("a", "b", "c", "d");

        Show(rows, "d", "c", "b", "a");

        Assert.Equal(["d", "c", "b", "a"], Names(rows));
    }

    /// <summary>
    /// A list holding one thing twice, which is what a reading gives where the machine will not say
    /// enough to tell two things apart. It ends holding what arrived rather than what it started
    /// with, in both directions.
    /// </summary>
    [Fact]
    public void OneThingHeldTwiceSettlesToWhatArrived()
    {
        var rows = Rows("a", "a", "b");

        Show(rows, "a", "b");

        Assert.Equal(["a", "b"], Names(rows));

        Show(rows, "a", "a", "b");

        Assert.Equal(["a", "a", "b"], Names(rows));
    }

    [Fact]
    public void AValueThatHasNotChangedIsLeftAlone()
    {
        var rows = new ObservableCollection<Held>([new("a", 1), new("b", 2)]);
        var watched = Watch(rows);

        LiveList.Show(rows, [new Held("a", 1), new Held("b", 2)], held => held.Name);

        Assert.Empty(watched);
    }

    /// <summary>
    /// A value cannot be written over, so one that is still here and no longer equal is replaced
    /// where it sits. That costs the one entry, where removing and inserting it would cost two
    /// changes and lose whichever of them the list had selected.
    /// </summary>
    [Fact]
    public void AValueThatHasChangedIsReplacedWhereItSits()
    {
        var rows = new ObservableCollection<Held>([new("a", 1), new("b", 2)]);
        var watched = Watch(rows);

        LiveList.Show(rows, [new Held("a", 1), new Held("b", 20)], held => held.Name);

        Assert.Equal([NotifyCollectionChangedAction.Replace], watched);
        Assert.Equal(new Held("b", 20), rows[1]);
    }

    [Fact]
    public void SentencesAreWrittenOverWhereTheySit()
    {
        var notes = new ObservableCollection<string>(["one", "two", "three"]);
        var watched = Watch(notes);

        LiveList.Rewrite(notes, ["one", "changed", "three"]);

        Assert.Equal([NotifyCollectionChangedAction.Replace], watched);
        Assert.Equal(["one", "changed", "three"], notes);
    }

    [Fact]
    public void SentencesShrinkAndGrowFromTheEnd()
    {
        var notes = new ObservableCollection<string>(["one", "two", "three"]);

        LiveList.Rewrite(notes, ["one"]);
        Assert.Equal(["one"], notes);

        LiveList.Rewrite(notes, ["one", "two"]);
        Assert.Equal(["one", "two"], notes);
    }

    private static ObservableCollection<Row> Rows(params string[] names) =>
        new(names.Select(name => new Row(name)));

    private static void Show(ObservableCollection<Row> rows, params string[] names) =>
        LiveList.Show(rows, names, row => row.Name, name => name, name => new Row(name), (_, _) => { });

    private static IEnumerable<string> Names(IEnumerable<Row> rows) => rows.Select(row => row.Name);

    /// <summary>Every change the collection reports, in the order it reports them.</summary>
    private static List<NotifyCollectionChangedAction> Watch<T>(ObservableCollection<T> rows)
    {
        var watched = new List<NotifyCollectionChangedAction>();

        rows.CollectionChanged += (_, e) => watched.Add(e.Action);

        return watched;
    }

    /// <summary>A row that can be written over, as a view-model's rows are.</summary>
    private sealed class Row(string name)
    {
        public string Name { get; } = name;

        public int Held { get; set; }
    }

    /// <summary>A row that is a value, as a drive choice and a kept item are.</summary>
    private sealed record Held(string Name, int Bytes);
}
