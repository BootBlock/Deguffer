using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Memory;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One line of the memory list: what a part or a process is, and how much it holds.
///
/// <para>The list is the same contents as the picture, for a reader who cannot use a picture. It says
/// what each thing is and what it holds, and classifies nothing: it never says a row is unneeded,
/// idle or safe to close, and it is never ordered by how closable anything is (§7.2). A row may be
/// picked, and <see cref="MemorySelection"/> is what a pick means.</para>
///
/// <para>The same row serves the list and the tree. In the list it is a step on a route: the reader
/// goes inside it and the rows are replaced by what it held. In the tree it opens where it sits and
/// <see cref="Children"/> is what appears under it.</para>
/// </summary>
public sealed partial class MemoryRow : ObservableObject
{
    public MemoryRow(MemoryTree tree, int node, long partTotal)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Node = node;
        Key = tree.KeyOf(node);

        Describe(tree, node, partTotal);
    }

    /// <summary>The node in the tree on screen. It changes with every refresh, so it is rewritten rather than compared.</summary>
    public int Node { get; private set; }

    /// <summary>What this row is about, whichever tree is on screen. See <see cref="MemoryPlace"/>.</summary>
    public MemoryNodeKey Key { get; }

    /// <summary>
    /// What this row holds, for the tree view, which shows a row's contents under the row rather
    /// than by going inside it.
    ///
    /// <para>Filled by <see cref="MemoryViewModel"/> while the tree is what is on screen, and
    /// brought up to date in place like the rows themselves: a reading arrives every couple of
    /// seconds, and a reader who has opened three levels keeps them only if nothing under them is
    /// rebuilt. Left alone while the list or a picture is on screen, because nothing is reading it,
    /// and brought up to date again the moment the tree is chosen.</para>
    /// </summary>
    public ObservableCollection<MemoryRow> Children { get; } = [];

    /// <summary>
    /// Whether the reader has opened this row in the tree view. Held here rather than in the
    /// control, because the control's own copy goes when its container is recycled, and because it
    /// is what decides how far down a reading fills <see cref="Children"/> in.
    /// </summary>
    public bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opens))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Held { get; set; } = string.Empty;

    /// <summary>How much of what holds it this is, from 0 to 100, for the bar on the row.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    /// <summary>What this is, for a reader who does not recognise the name.</summary>
    [ObservableProperty]
    public partial string Explanation { get; set; } = string.Empty;

    /// <summary>
    /// Whether this row holds anything, so the list says which rows open into something.
    ///
    /// <para>The list is the route through the tree for a reader who cannot use the picture, where
    /// nesting is not visible. Both channels carry it: the chevron for a reader looking at the list,
    /// and <see cref="Description"/> in words for one who is not.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial bool HasChildren { get; set; }

    /// <summary>What a screen reader announces for the whole row.</summary>
    public string Description => HasChildren ? $"{Name}, {Held}, holds more" : $"{Name}, {Held}";

    /// <summary>What the chevron on the row does, said about this row rather than about "this".</summary>
    public string Opens => $"Show what {Name} holds";

    /// <summary>Bring the row up to date from a newer tree, without replacing the row itself.</summary>
    public void Describe(MemoryTree tree, int node, long partTotal)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Node = node;
        Name = MemoryText.Name(tree, node);
        Held = MemoryText.Figures(tree, node);
        Share = partTotal > 0 ? 100.0 * tree.SizeOf(node) / partTotal : 0;
        Explanation = MemoryPartGuide.Describe(tree, node);
        HasChildren = tree.IsContainer(node);

        OnPropertyChanged(nameof(Description));
    }
}
