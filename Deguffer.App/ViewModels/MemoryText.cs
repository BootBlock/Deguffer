using Deguffer.Core.Memory;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What one node of a <see cref="MemoryTree"/> is called on screen, and what its figures read as.
///
/// <para>Here rather than in the tree, because a name is presentation: the tree holds a process's
/// image name and the services it hosts, and this decides that a process's own share reads as the
/// process "itself". What a service host is called is <see cref="ServiceHostText"/>'s, because §7.2
/// states that one as a rule and the words that promise it are beside it.</para>
/// </summary>
internal static class MemoryText
{
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

    /// <summary>The name a shape carries on the picture: what it is, and how much it holds.</summary>
    public static string Label(MemoryTree tree, int node) =>
        $"{Name(tree, node)}  {FreeSpace.Format(tree.SizeOf(node))}";

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
