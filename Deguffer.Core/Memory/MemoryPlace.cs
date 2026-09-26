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

        if (standing is null || !standing.Holds(node))
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

    /// <summary>
    /// Whether a view moving from <paramref name="node"/> of <paramref name="standing"/> to
    /// <paramref name="next"/> of <paramref name="arriving"/> is still looking at the same thing.
    ///
    /// <para>What decides whether a list on screen is brought up to date where it stands or started
    /// again: the same thing measured again keeps the reader's place in it, and another thing has no
    /// place worth keeping. By <see cref="TryCarry"/>'s rule and no looser one, so a node whose number
    /// happens to match in the arriving tree is not taken for the same thing.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing has been read yet.</param>
    public static bool Continues(MemoryTree? standing, int node, MemoryTree arriving, int next) =>
        TryCarry(standing, node, arriving) == next;

    /// <summary>
    /// Where a view stands once the reader opens <paramref name="node"/>, or null where there is
    /// nothing inside it to show.
    ///
    /// <para>A node that holds nothing is not a place to stand: a view standing there would show an
    /// empty list and a picture of nothing, with no way to say which. A number outside the tree is
    /// one from a reading that has been replaced, and names nothing here.</para>
    /// </summary>
    public static int? Inside(MemoryTree tree, int node)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return tree.Holds(node) && tree.IsContainer(node) ? node : null;
    }
}
