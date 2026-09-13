using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Configuration;
using Deguffer.Core.Memory;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the Memory page: reads where memory is every couple of seconds, builds the tree from each
/// read, and keeps the reader on whatever they were looking at.
///
/// <para>It shows and explains, and offers nothing to do (§7.2). There is no command here, no
/// selection, and nothing that reaches a process.</para>
///
/// <para>The page is pointed at one node of one tree, as Explore's is, and everything on screen is
/// rebuilt from that pair: the headline, the rows, the trail and the picture.</para>
/// </summary>
public sealed partial class MemoryViewModel(MemoryFeed feed) : ObservableObject
{
    /// <summary>Where memory is, as of the last read, or null before the first one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTree))]
    [NotifyPropertyChangedFor(nameof(HasNoTree))]
    public partial MemoryTree? Tree { get; set; }

    /// <summary>Which node the page is showing the contents of.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAscend))]
    public partial int CurrentNode { get; set; }

    /// <summary>Commit charge against the commit limit: the figure §7.2 leads with.</summary>
    [ObservableProperty]
    public partial string Commit { get; set; } = "Reading where memory is…";

    [ObservableProperty]
    public partial string Available { get; set; } = string.Empty;

    /// <summary>How much of the commit limit is committed, 0 to 100, for the bar beside the words.</summary>
    [ObservableProperty]
    public partial double Committed { get; set; }

    /// <summary>What went wrong, where the machine would not answer at all. Empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string Failure { get; set; } = string.Empty;

    /// <summary>Which picture the reader asked for.</summary>
    [ObservableProperty]
    public partial ExploreView SelectedView { get; set; }

    /// <summary>What the pointer is over, and what that is.</summary>
    [ObservableProperty]
    public partial string Hovered { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HoveredFigures { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoveredNote))]
    public partial string HoveredNote { get; set; } = string.Empty;

    /// <summary>What the current node holds, largest first.</summary>
    public ObservableCollection<MemoryRow> Rows { get; } = [];

    /// <summary>The trail from the root down to the current node.</summary>
    public ObservableCollection<MemoryCrumb> Trail { get; } = [];

    /// <summary>What this read could not measure, and that its figures are lower bounds (§7.2).</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public bool HasTree => Tree is not null;

    public bool HasNoTree => Tree is null;

    public bool HasFailure => Failure.Length > 0;

    public bool HasHoveredNote => HoveredNote.Length > 0;

    public bool CanAscend => Tree is { } tree && CurrentNode != tree.RootNode;

    /// <summary>Raised once the tree, the node or the rows have changed, so the page redraws once.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>
    /// Read where memory is until <paramref name="ct"/> is cancelled, which the page does when the
    /// reader leaves it. One read at a time, on the cadence <see cref="MemoryFeed"/> keeps.
    /// </summary>
    public async Task WatchAsync(CancellationToken ct)
    {
        // A visit starts with whatever the last one ended with cleared: a refusal is reported against
        // the reading that met it, and leaving it up would state it about this one.
        Failure = string.Empty;

        try
        {
            await foreach (var snapshot in feed.ReadAsync(ct).ConfigureAwait(true))
            {
                // A read already past its own last check still finishes, so without this a reading
                // taken for a page the reader has left could be drawn over a newer one.
                ct.ThrowIfCancellationRequested();

                Show(MemoryTreeBuilder.Build(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left. Nothing was half-done: a snapshot is read whole or not at all.
        }
        catch (Win32Exception ex)
        {
            // Windows refused one of the calls the snapshot is made of. There is nothing to show and
            // nothing to retry on this page's own account, so it says so and stops.
            Failure = $"Windows would not answer: {ex.Message}";
        }
        finally
        {
            // Anything this method does not name stops the readings without it seeing them, and the
            // last tree would then stand on screen as though it were the present. §7.2 turns on a
            // reader knowing how old a figure is, so a stopped page says it has stopped. The
            // exception itself is left to propagate rather than swallowed (G3).
            if (!ct.IsCancellationRequested && Failure.Length == 0)
            {
                Failure = "The readings have stopped. What is on screen is the last one taken.";
            }
        }
    }

    /// <summary>Show what is inside <paramref name="node"/>, where anything is.</summary>
    public void Descend(int node)
    {
        if (Tree is { } tree && node >= 0 && node < tree.NodeCount && tree.IsContainer(node))
        {
            Show(tree, node);
        }
    }

    public void Ascend()
    {
        if (Tree is { } tree && CurrentNode != tree.RootNode)
        {
            Show(tree, tree.ParentOf(CurrentNode));
        }
    }

    /// <summary>
    /// Jump to a step on the trail, wherever the reading on screen numbered it.
    ///
    /// <para>By what the crumb is rather than by a node number it was built with: the trail outlives
    /// the tree that made it, and every reading renumbers the nodes.</para>
    /// </summary>
    public void GoTo(MemoryCrumb crumb)
    {
        ArgumentNullException.ThrowIfNull(crumb);

        if (Tree is { } tree && tree.Find(crumb.Key) is { } node)
        {
            Show(tree, node);
        }
    }

    /// <summary>
    /// Say what the pointer is over. Called on every move that lands on a different shape, so it
    /// formats and assigns and does nothing else.
    /// </summary>
    public void Hover(int? node, long? aggregateBytes)
    {
        (Hovered, HoveredFigures, HoveredNote) = (Tree, node, aggregateBytes) switch
        {
            (_, _, { } bytes) => (
                "Parts too small to draw separately", Core.Scanning.FreeSpace.Format(bytes), string.Empty),

            ({ } tree, { } over, _) when over >= 0 && over < tree.NodeCount => (
                MemoryText.Name(tree, over),
                MemoryText.Figures(tree, over),
                MemoryPartGuide.Describe(tree.PartOf(over))),

            _ => (string.Empty, string.Empty, string.Empty),
        };
    }

    /// <summary>What to write on a shape of the tree on screen.</summary>
    public string LabelFor(int node) =>
        Tree is { } tree && node >= 0 && node < tree.NodeCount ? MemoryText.Label(tree, node) : string.Empty;

    /// <summary>
    /// Take a new reading: keep the reader where they were, and rebuild everything on screen from it.
    /// </summary>
    private void Show(MemoryTree tree) => Show(tree, MemoryPlace.Carry(Tree, CurrentNode, tree));

    private void Show(MemoryTree tree, int node)
    {
        var standing = Tree;

        // Read before the assignment below, and not after it. TryCarry reads a node number of the
        // tree on screen, and once CurrentNode holds a number of the arriving tree that same number
        // means something else entirely.
        var sameThing = standing is not null && MemoryPlace.TryCarry(standing, CurrentNode, tree) == node;

        Tree = tree;
        CurrentNode = node;
        Failure = string.Empty;

        var system = tree.Snapshot.System;

        Commit = MemoryHeadline.Commit(system);
        Available = MemoryHeadline.Available(system);
        Committed = MemoryHeadline.CommittedFraction(system) * 100;

        ShowNotes(tree);
        ShowRows(tree, node, sameThing);
        ShowTrail(tree, node);

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowNotes(MemoryTree tree)
    {
        var notes = MemoryNotes.For(tree);

        // Rewritten in place while the sentences have not changed, which is every refresh of a machine
        // whose figures are all readable: clearing the collection would flicker the panel twice a
        // second.
        for (var i = 0; i < notes.Count; i++)
        {
            if (i < Notes.Count)
            {
                if (Notes[i] != notes[i])
                {
                    Notes[i] = notes[i];
                }
            }
            else
            {
                Notes.Add(notes[i]);
            }
        }

        while (Notes.Count > notes.Count)
        {
            Notes.RemoveAt(Notes.Count - 1);
        }
    }

    /// <param name="sameThing">
    /// Whether the rows on screen are about the same node of the same thing, in which case they are
    /// brought up to date in place. Clearing the collection is a reset for the bound list, which throws
    /// away the scroll position and the reader's place twice a second.
    /// </param>
    private void ShowRows(MemoryTree tree, int node, bool sameThing)
    {
        if (!sameThing)
        {
            Rows.Clear();
        }

        // What the rows are a share of, so a row's bar answers "how much of this part is that".
        var partTotal = tree.SizeOf(node);

        var at = 0;

        foreach (var child in tree.ChildrenOf(node))
        {
            if (at < Rows.Count && Rows[at].Is(tree, child))
            {
                Rows[at].Describe(tree, child, partTotal);
            }
            else if (RowFor(tree, child, at) is { } sits)
            {
                Rows.Move(sits, at);
                Rows[at].Describe(tree, child, partTotal);
            }
            else
            {
                Rows.Insert(at, new MemoryRow(tree, child, partTotal));
            }

            at++;
        }

        while (Rows.Count > at)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    /// <summary>
    /// Where the row for <paramref name="child"/> sits at or after <paramref name="from"/>, or null
    /// where none of them is about it. What it costs is how far the rows actually moved, which between
    /// two readings of one machine is nothing at all for most of them.
    /// </summary>
    private int? RowFor(MemoryTree tree, int child, int from)
    {
        for (var i = from; i < Rows.Count; i++)
        {
            if (Rows[i].Is(tree, child))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The trail from the root down to <paramref name="node"/>, brought up to date in place.
    ///
    /// <para>Rewritten rather than cleared for the reason <see cref="ShowNotes"/> and
    /// <see cref="ShowRows"/> are, and one more: a crumb is a button a reader can put the keyboard on,
    /// and clearing the collection destroys it. On a page that takes a reading every couple of
    /// seconds that would move the focus off the trail before anyone could use it.</para>
    /// </summary>
    private void ShowTrail(MemoryTree tree, int node)
    {
        var steps = new List<int>();

        for (var current = node; ; current = tree.ParentOf(current))
        {
            steps.Add(current);

            if (current == tree.RootNode)
            {
                break;
            }
        }

        steps.Reverse();

        for (var i = 0; i < steps.Count; i++)
        {
            if (i >= Trail.Count)
            {
                Trail.Add(new MemoryCrumb(tree, steps[i]));
            }
            else if (Trail[i].Is(tree, steps[i]))
            {
                Trail[i].Describe(tree);
            }
            else
            {
                // A different thing at this step, which is a reader who has navigated. The button is
                // rebuilt, and that is right: it is not the one they were on.
                Trail[i] = new MemoryCrumb(tree, steps[i]);
            }
        }

        while (Trail.Count > steps.Count)
        {
            Trail.RemoveAt(Trail.Count - 1);
        }
    }
}
