using Deguffer.Core.Memory;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What one node of a <see cref="MemoryTree"/> is called on screen, and what its figures read as.
///
/// <para>Here rather than in the tree, because a name is presentation: the tree holds a process's
/// image name and the services it hosts, and this decides that a host with one service reads as that
/// service, and that a process's own share reads as the process "itself".</para>
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
            MemoryPart.Process => WithItsServices(name, tree.ServicesOf(node)),
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

    /// <summary>
    /// A host named by the service it holds, which is what a reader recognises: several hosts share
    /// one image name, and the service is the part they came for.
    /// </summary>
    private static string WithItsServices(string name, IReadOnlyList<RunningService> services) => services.Count switch
    {
        0 => name,
        1 => $"{name}: {services[0].DisplayName}",
        _ => $"{name}: {services.Count} services",
    };
}
