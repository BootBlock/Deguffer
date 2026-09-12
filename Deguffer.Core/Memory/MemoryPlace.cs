namespace Deguffer.Core.Memory;

/// <summary>
/// Where a view standing on one node of a <see cref="MemoryTree"/> should stand once a refresh
/// replaces the tree.
///
/// <para>By what the node is, never by its number, its name or its place. A refresh renumbers every
/// node, several processes share an image name, and a process moves when its parent exits. A process
/// is the same process only where both its identifier and its creation time match, because Windows
/// reuses identifiers (§7.2).</para>
/// </summary>
public static class MemoryPlace
{
    /// <summary>
    /// The node of <paramref name="arriving"/> that is what <paramref name="node"/> was in
    /// <paramref name="standing"/>, or null where it has gone.
    ///
    /// <para>A process's own share is found as the process itself once its children have gone, because
    /// the share is then drawn as the process's own node rather than beside anything.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing has been read yet.</param>
    public static int? TryCarry(MemoryTree? standing, int node, MemoryTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        if (standing is null || node < 0 || node >= standing.NodeCount)
        {
            return null;
        }

        var key = standing.KeyOf(node);

        return arriving.Find(key)
            ?? (key.Part == MemoryPart.OwnShare ? arriving.Find(key with { Part = MemoryPart.Process }) : null);
    }

    /// <summary>The same, standing on the root where nothing matches, for a view that has to stand somewhere.</summary>
    public static int Carry(MemoryTree? standing, int node, MemoryTree arriving) =>
        TryCarry(standing, node, arriving) ?? arriving.RootNode;
}
