using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Memory;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One step of the trail from the root down to what is on screen.
///
/// <para>A class that is written over rather than a value that is replaced, for the reason
/// <see cref="MemoryRow"/> is one: a reading arrives every couple of seconds and renumbers every
/// node, so a crumb carrying its node number as part of its identity would be a new crumb every
/// time. Replacing it rebuilds the button, which takes the keyboard's focus off it.</para>
/// </summary>
public sealed partial class MemoryCrumb : ObservableObject
{
    public MemoryCrumb(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Key = tree.KeyOf(node);
        Name = MemoryText.Name(tree, node);
    }

    /// <summary>What this crumb is about, whichever tree is on screen. See <see cref="MemoryPlace"/>.</summary>
    public MemoryNodeKey Key { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    /// <summary>Bring the crumb up to date from a newer tree, without replacing the crumb itself.</summary>
    public void Describe(MemoryTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (tree.Find(Key) is { } node)
        {
            Name = MemoryText.Name(tree, node);
        }
    }

    /// <summary>Whether this crumb is about <paramref name="node"/> of <paramref name="tree"/>.</summary>
    public bool Is(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Key == tree.KeyOf(node);
    }
}
