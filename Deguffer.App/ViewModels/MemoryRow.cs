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

    [ObservableProperty]
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
    /// nesting is not visible. Both channels carry it: the glyph for a reader looking at the list, and
    /// <see cref="Description"/> in words for one who is not.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial bool HasChildren { get; set; }

    /// <summary>
    /// A chevron on a row that opens, and nothing on one that does not. Segoe Fluent Icons, and never
    /// the only thing carrying the distinction — <see cref="Description"/> says it in words.
    /// </summary>
    public string Icon => HasChildren ? "\uE76C" : string.Empty;

    /// <summary>What a screen reader announces for the whole row.</summary>
    public string Description => HasChildren ? $"{Name}, {Held}, holds more" : $"{Name}, {Held}";

    /// <summary>Bring the row up to date from a newer tree, without replacing the row itself.</summary>
    public void Describe(MemoryTree tree, int node, long partTotal)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Node = node;
        Name = MemoryText.Name(tree, node);
        Held = MemoryText.Figures(tree, node);
        Share = partTotal > 0 ? 100.0 * tree.SizeOf(node) / partTotal : 0;
        Explanation = MemoryPartGuide.Describe(tree.PartOf(node));
        HasChildren = tree.IsContainer(node);

        OnPropertyChanged(nameof(Description));
    }
}
