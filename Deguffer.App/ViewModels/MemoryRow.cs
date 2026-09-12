using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Memory;

namespace Deguffer.App.ViewModels;

/// <summary>One line of the memory list: what a part or a process is, and how much it holds.</summary>
///
/// <para>The list is the same contents as the picture, for a reader who cannot use a picture. It says
/// what each thing is and what it holds, and offers nothing to do about it (§7.2).</para>
public sealed partial class MemoryRow : ObservableObject
{
    public MemoryRow(MemoryTree tree, int node, long partTotal)
    {
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

    /// <summary>What a screen reader announces for the whole row.</summary>
    public string Description => $"{Name}, {Held}";

    /// <summary>Whether this row holds anything, so the list can say which have nothing under them.</summary>
    public bool HasChildren { get; private set; }

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

    /// <summary>Whether this row is about <paramref name="node"/> of <paramref name="tree"/>.</summary>
    public bool Is(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Key == tree.KeyOf(node);
    }
}
