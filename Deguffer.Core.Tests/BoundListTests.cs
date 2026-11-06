using System.Collections.Specialized;
using System.ComponentModel;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a bound list is told when it is rebuilt for something else. A list that is cleared and then
/// filled a row at a time arrives at the same contents through one reset and a change per row, and
/// each of those is work for the control on the UI thread; see <see cref="BoundList{T}"/>.
/// </summary>
public sealed class BoundListTests
{
    /// <summary>The case the type exists for: thousands of rows, one change.</summary>
    [Fact]
    public void ReplacingTellsTheListOnceWithAReset()
    {
        var rows = new BoundList<int> { 1, 2, 3 };
        var told = Watch(rows);

        rows.Replace(Enumerable.Range(0, 10_000));

        Assert.Equal([NotifyCollectionChangedAction.Reset], told);
        Assert.Equal(Enumerable.Range(0, 10_000), rows);
    }

    [Fact]
    public void ReplacingAnEmptyListTellsItOnce()
    {
        var rows = new BoundList<int>();
        var told = Watch(rows);

        rows.Replace([4, 5]);

        Assert.Equal([NotifyCollectionChangedAction.Reset], told);
        Assert.Equal([4, 5], rows);
    }

    [Fact]
    public void ReplacingWithNothingEmptiesTheListInOneReset()
    {
        var rows = new BoundList<int> { 1, 2, 3 };
        var told = Watch(rows);

        rows.Replace([]);

        Assert.Equal([NotifyCollectionChangedAction.Reset], told);
        Assert.Empty(rows);
    }

    [Fact]
    public void AListThatWasEmptyAndStaysEmptyIsToldNothing()
    {
        var rows = new BoundList<int>();
        var told = Watch(rows);
        var properties = new List<string?>();
        ((INotifyPropertyChanged)rows).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        rows.Replace([]);

        Assert.Empty(told);
        Assert.Empty(properties);
    }

    /// <summary>A binding to the count or to an index reads the list again only when it is told to.</summary>
    [Fact]
    public void ReplacingTellsBindingsTheCountAndTheIndexerChanged()
    {
        var rows = new BoundList<int> { 1 };
        var properties = new List<string?>();
        ((INotifyPropertyChanged)rows).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        rows.Replace([7, 8, 9]);

        Assert.Equal(["Count", "Item[]"], properties);
    }

    /// <summary>
    /// A row that throws while it is made leaves the list holding what was made before it. The list
    /// is still told, because a control that was not told what it holds goes on to read a row
    /// that is not there.
    /// </summary>
    [Fact]
    public void AListIsToldWhatItHoldsEvenWhenMakingARowThrows()
    {
        var rows = new BoundList<int> { 1, 2, 3 };
        var told = Watch(rows);

        Assert.Throws<InvalidOperationException>(() => rows.Replace(Failing()));

        Assert.Equal([NotifyCollectionChangedAction.Reset], told);
        Assert.Equal([10], rows);

        static IEnumerable<int> Failing()
        {
            yield return 10;

            throw new InvalidOperationException("The second row could not be made.");
        }
    }

    /// <summary>The guard every other change on the collection has: a handler may not change the list it is being told about.</summary>
    [Fact]
    public void AHandlerCannotReplaceTheListWhileTwoAreBeingTold()
    {
        var rows = new BoundList<int> { 1 };
        var tried = false;
        Exception? thrown = null;

        rows.CollectionChanged += (_, _) =>
        {
            if (!tried)
            {
                tried = true;
                thrown = Record.Exception(() => rows.Replace([3]));
            }
        };
        rows.CollectionChanged += (_, _) => { };

        rows.Add(2);

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Equal([1, 2], rows);
    }

    private static List<NotifyCollectionChangedAction> Watch(INotifyCollectionChanged list)
    {
        var told = new List<NotifyCollectionChangedAction>();

        list.CollectionChanged += (_, e) => told.Add(e.Action);

        return told;
    }
}
