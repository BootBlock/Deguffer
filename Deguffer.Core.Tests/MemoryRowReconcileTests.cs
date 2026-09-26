using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Deguffer.Core.Memory;
using Deguffer.Testing;
using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Memory page's list of programs is told between two readings of the same machine
/// (issue #142). The page itself is in Deguffer.App and has no test project, so this stands in for
/// it: two trees built by <see cref="MemoryTreeBuilder"/> from two snapshots, reconciled by the same
/// call the page makes, with the changes counted.
///
/// <para>A process starting or exiting is the ordinary case, not the rare one: on a machine running
/// a few hundred programs, one of them comes or goes most times the page reads. If that costs the
/// list a change per row, the list rebuilds under the reader every couple of seconds.</para>
/// </summary>
public sealed class MemoryRowReconcileTests
{
    [Fact]
    public void AProcessExitingCostsTheListOneRemoval()
    {
        // Second of four, so a pass that matched the rows by position alone would answer its
        // absence by sliding it past the two below it rather than by removing it.
        var before = Applications(Machine().Process(500, 1, "going.exe", 900, created: 5));
        var after = Applications(Machine());

        var rows = Show(before);
        var watched = Watch(rows);

        Show(after, rows);

        Assert.Equal([NotifyCollectionChangedAction.Remove], watched);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void AProcessStartingCostsTheListOneInsertion()
    {
        var before = Applications(Machine());
        var after = Applications(Machine().Process(500, 1, "new.exe", 400, created: 5));

        var rows = Show(before);
        var watched = Watch(rows);

        Show(after, rows);

        Assert.Equal([NotifyCollectionChangedAction.Add], watched);
    }

    /// <summary>
    /// The figures move on every reading and the children are in size order, so two programs that
    /// change places are the other ordinary case. It costs the one move, and both rows are the rows
    /// that were already there.
    /// </summary>
    [Fact]
    public void TwoProgramsChangingPlacesCostsTheListOneMove()
    {
        var before = Applications(Machine());
        var after = Applications(
            new MemorySnapshotBuilder()
                .Process(100, 1, "alpha.exe", 900, created: 1)
                .Process(200, 1, "beta.exe", 1_100, created: 2)
                .Process(300, 1, "gamma.exe", 700, created: 3));

        var rows = Show(before);
        var alpha = rows[0];
        var beta = rows[1];
        var watched = Watch(rows);

        Show(after, rows);

        Assert.Equal([NotifyCollectionChangedAction.Move], watched);
        Assert.Same(beta, rows[0]);
        Assert.Same(alpha, rows[1]);
    }

    [Fact]
    public void AReadingThatChangesNothingButTheFiguresTellsTheListNothing()
    {
        var before = Applications(Machine());
        var after = Applications(
            new MemorySnapshotBuilder()
                .Process(100, 1, "alpha.exe", 1_001, created: 1)
                .Process(200, 1, "beta.exe", 801, created: 2)
                .Process(300, 1, "gamma.exe", 701, created: 3));

        var rows = Show(before);
        var watched = Watch(rows);

        Show(after, rows);

        Assert.Empty(watched);
        Assert.Equal(1_001 * MemorySnapshotBuilder.MiB, rows[0].Bytes);
    }

    /// <summary>Three programs, plus the six parts of Windows every snapshot carries.</summary>
    private static MemorySnapshotBuilder Machine() =>
        new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 1_000, created: 1)
            .Process(200, 1, "beta.exe", 800, created: 2)
            .Process(300, 1, "gamma.exe", 700, created: 3);

    /// <summary>The node holding every program, which is the list the issue is about.</summary>
    private static (MemoryTree Tree, int Node) Applications(MemorySnapshotBuilder machine)
    {
        var tree = MemoryTreeBuilder.Build(machine.Build());

        return (tree, tree.Find(MemoryNodeKey.Of(MemoryPart.Applications))!.Value);
    }

    /// <summary>The call <c>MemoryViewModel.ShowRows</c> makes, against rows of the same shape.</summary>
    private static ObservableCollection<Row> Show(
        (MemoryTree Tree, int Node) reading, ObservableCollection<Row>? rows = null)
    {
        var (tree, node) = reading;
        var into = rows ?? [];
        var arriving = new List<int>();

        foreach (var child in tree.ChildrenOf(node))
        {
            arriving.Add(child);
        }

        LiveList.Show(
            into,
            arriving,
            row => row.Key,
            child => tree.KeyOf(child),
            child => new Row(tree.KeyOf(child), tree.SizeOf(child)),
            (row, child) => row.Bytes = tree.SizeOf(child));

        return into;
    }

    private static List<NotifyCollectionChangedAction> Watch<T>(ObservableCollection<T> rows)
    {
        var watched = new List<NotifyCollectionChangedAction>();

        rows.CollectionChanged += (_, e) => watched.Add(e.Action);

        return watched;
    }

    /// <summary>A row as the page holds one: its identity fixed, its figures re-read per reading.</summary>
    private sealed class Row(MemoryNodeKey key, long bytes)
    {
        public MemoryNodeKey Key { get; } = key;

        public long Bytes { get; set; } = bytes;
    }
}
