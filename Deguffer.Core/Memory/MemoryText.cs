using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Memory;

/// <summary>
/// What one node of a <see cref="MemoryTree"/> is called wherever the page names it, and what its
/// figures read as: in its rows and trail, on the line under its map and to a screen reader.
///
/// <para>Apart from the tree, because a name is wording: the tree holds a process's image name and
/// the services it hosts, and this decides that a process's own share reads as the process
/// "itself". What a service host is called is <see cref="ServiceHostText"/>'s, because §7.2 states
/// that one as a rule and the words that promise it are beside it.</para>
/// </summary>
public static class MemoryText
{
    /// <summary>
    /// The line under the map, where the pointer or the keyboard is on <paramref name="node"/>: its
    /// name, its figures, and what part of memory it is.
    /// </summary>
    public static MapReadout Readout(MemoryTree tree, int node) =>
        new(Name(tree, node), Figures(tree, node), MemoryPartGuide.Describe(tree, node));

    /// <summary>
    /// What a screen reader is told of <paramref name="node"/>: its name, what part of memory it is,
    /// and whether opening it shows what it holds. Nothing of removing: what the page does to a
    /// program is close it, which its own button says (§7.2.1).
    /// </summary>
    /// <remarks>
    /// No shape is marked as a lower bound, because every figure on the page is one and the page
    /// says so once (<see cref="MemoryNotes"/>, §7.2). Marked on each shape, the mark says nothing.
    /// </remarks>
    public static MapShapeFacts Facts(MemoryTree tree, int node) =>
        new(Name(tree, node), Kind(tree, node), MemoryPlace.Inside(tree, node) is not null, LowerBound: false);

    /// <summary>What to call <paramref name="node"/>.</summary>
    public static string Name(MemoryTree tree, int node)
    {
        var name = tree.NameOf(node);

        return tree.PartOf(node) switch
        {
            MemoryPart.OwnShare => $"{name} itself",
            MemoryPart.Process => ServiceHostText.Name(name, tree.ServicesOf(node)),
            _ => name,
        };
    }

    /// <summary>What part of memory <paramref name="node"/> is, as a screen reader names its kind.</summary>
    public static string Kind(MemoryTree tree, int node) => tree.PartOf(node) switch
    {
        MemoryPart.Process => "Program",
        MemoryPart.OwnShare => "The program's own memory",
        MemoryPart.Applications or MemoryPart.Services => "Programs",
        _ => "Memory",
    };

    /// <summary>What a shape carries on the picture: what it is, and how much it holds.</summary>
    public static MapCaption Caption(MemoryTree tree, int node) => MapShapeText.Caption(Name(tree, node), tree.SizeOf(node));

    /// <summary>
    /// How much a node holds, and for a process what it has committed beside it.
    ///
    /// <para>The two are different questions: what is in memory now, and what the process has asked
    /// for, which is the figure the headline leads with.</para>
    /// </summary>
    public static string Figures(MemoryTree tree, int node)
    {
        var held = FreeSpace.Format(tree.SizeOf(node));

        return tree.ProcessOf(node) is { CommitCharge: { } committed }
            ? $"{held} in memory, {FreeSpace.Format(committed)} committed"
            : held;
    }
}
