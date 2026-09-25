namespace Deguffer.Core.Exploring;

/// <summary>
/// Where the views are in a scanned tree: on which node, and, on the root of a scan of a whole
/// volume, whether they are on the volume itself or inside the root.
///
/// <para>A scan of a whole volume is drawn one level above its root: the root is one block, beside
/// the volume's free space and the use the scan did not account for (see <see cref="VolumeSpace"/>).
/// Opening the root is going into that block, like going into any folder, so the picture is then the
/// root alone across the whole canvas, with nothing beside it. The node is the same either way, so
/// the node alone cannot say which of the two the reader is looking at, and this says it.</para>
///
/// <para>So the volume is a level of its own, above the root, with a step of its own on the trail
/// (see <see cref="Trail"/>). Going up goes one step back along the trail, whatever route led there:
/// from a folder in the root to the root, from the root to the volume.</para>
///
/// <para>Being on the volume is honoured only where there is a volume: a scan of a folder, or a scan
/// still running, draws nothing beside its root, and the position is then simply its root. It is kept
/// through those rather than cleared, so a rescan of a drive comes back where the reader was.</para>
///
/// <para>In Core rather than in the page's view model, so the rules are provable without a window
/// (G8).</para>
/// </summary>
/// <param name="Node">The node the views are drawing.</param>
/// <param name="OnVolume">
/// Whether the views are on the volume rather than inside its root. Meaningful only on the root of a
/// scan of a whole volume.
/// </param>
public readonly record struct ExplorePosition(int Node, bool OnVolume)
{
    /// <summary>Where a tree is first shown: its root, on the volume where it is the whole of one.</summary>
    public static ExplorePosition Top(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return new ExplorePosition(tree.RootNode, OnVolume: true);
    }

    /// <summary>A node, inside the root: a folder, or the root itself opened.</summary>
    public static ExplorePosition Inside(int node) => new(node, OnVolume: false);

    /// <summary>
    /// Whether this is the volume, drawn with <paramref name="volume"/> beside the root of
    /// <paramref name="tree"/>.
    /// </summary>
    public bool IsVolume(ExploreTree tree, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return OnVolume && Node == tree.RootNode && volume != VolumeSpace.None;
    }

    /// <summary>
    /// Where opening <paramref name="node"/> leads, or null where it leads nowhere new: the root when
    /// the views are already inside it.
    /// </summary>
    public ExplorePosition? Opening(ExploreTree tree, int node, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return node != tree.RootNode || IsVolume(tree, volume) ? Inside(node) : null;
    }

    /// <summary>One step back along <see cref="Trail"/>, or null at its start.</summary>
    public ExplorePosition? Up(ExploreTree tree, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (Node != tree.RootNode)
        {
            return Inside(tree.ParentOf(Node));
        }

        return IsVolume(tree, volume) || volume == VolumeSpace.None ? null : Top(tree);
    }

    /// <summary>The volume to draw beside what is on screen: <paramref name="volume"/> on the volume, and nothing inside it.</summary>
    public VolumeSpace Beside(ExploreTree tree, VolumeSpace volume) =>
        IsVolume(tree, volume) ? volume : VolumeSpace.None;

    /// <summary>
    /// Write into <paramref name="steps"/> every step from the top of <paramref name="tree"/> to
    /// here, in order: the volume where there is one, then the root and each folder down to this node
    /// where the views are inside it.
    ///
    /// <para>Into the caller's list rather than a new one, because a walked scan redraws the trail
    /// with every snapshot it publishes (G5).</para>
    /// </summary>
    public void Trail(ExploreTree tree, VolumeSpace volume, List<ExplorePosition> steps)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(steps);

        steps.Clear();

        if (!IsVolume(tree, volume))
        {
            for (var current = Node; ; current = tree.ParentOf(current))
            {
                steps.Add(Inside(current));

                if (current == tree.RootNode)
                {
                    break;
                }
            }
        }

        if (volume != VolumeSpace.None)
        {
            steps.Add(Top(tree));
        }

        steps.Reverse();
    }

    /// <summary>
    /// Where to be in <paramref name="arriving"/> once it replaces <paramref name="leaving"/>: the
    /// same node where it is still there, by <see cref="ExplorePlace.Carry"/>, and on the root the
    /// same side of it, where the arriving tree is rooted in the same place. A tree rooted somewhere
    /// else opens at its top.
    /// </summary>
    public ExplorePosition CarriedTo(ExploreTree? leaving, ExploreTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        if (leaving is null || ExplorePlace.TryCarry(leaving, leaving.RootNode, arriving) is null)
        {
            return Top(arriving);
        }

        var node = ExplorePlace.Carry(leaving, Node, arriving);

        return node == arriving.RootNode
            ? new ExplorePosition(node, OnVolume: Node != leaving.RootNode || OnVolume)
            : Inside(node);
    }
}
