namespace Deguffer.Core.Exploring;

/// <summary>
/// Where the views stand in a scanned tree: on which node, and, on the root of a scan of a whole
/// volume, whether the reader has opened the root itself.
///
/// <para>A scan of a whole volume is drawn one level above its root: the root is one block, beside
/// the volume's free space and the use the scan did not account for (see <see cref="VolumeSpace"/>).
/// Opening the root is going into that block, like going into any folder, so the picture is then the
/// root alone across the whole canvas, with nothing beside it. The node is the same either way, so
/// the node alone cannot say which of the two the reader is looking at, and this says it.</para>
///
/// <para>Going up from the opened root goes back out to the volume, and going up from anywhere below
/// the root goes to the parent as it always did, keeping whichever of the two the root was. A
/// crumb for the root is the root as the reader left it.</para>
///
/// <para>In Core rather than in the page's view model, so the rules are provable without a window
/// (G8).</para>
/// </summary>
/// <param name="Node">The node the views are drawing.</param>
/// <param name="RootOpened">
/// Whether the root has been opened, so it is drawn without the volume beside it. Meaningful only
/// for a scan of a whole volume, and kept while the reader is below the root.
/// </param>
public readonly record struct ExploreStanding(int Node, bool RootOpened = false)
{
    /// <summary>
    /// Where opening <paramref name="node"/> of <paramref name="tree"/> leads, or null where it leads
    /// nowhere new: the root that is already open, or one with no volume drawn beside it.
    /// </summary>
    /// <param name="volume">The volume the tree covers the whole of, or <see cref="VolumeSpace.None"/>.</param>
    public ExploreStanding? Opening(ExploreTree tree, int node, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (node != tree.RootNode)
        {
            return this with { Node = node };
        }

        return Node == tree.RootNode && !RootOpened && volume != VolumeSpace.None
            ? this with { RootOpened = true }
            : null;
    }

    /// <summary>One level up in <paramref name="tree"/>, or null at the top.</summary>
    public ExploreStanding? Up(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (Node != tree.RootNode)
        {
            return this with { Node = tree.ParentOf(Node) };
        }

        return RootOpened ? this with { RootOpened = false } : null;
    }

    /// <summary>
    /// The volume to draw beside what is on screen: <paramref name="volume"/> on the root of the tree
    /// before it is opened, and nothing anywhere else.
    /// </summary>
    public VolumeSpace Beside(ExploreTree tree, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return Node == tree.RootNode && !RootOpened ? volume : VolumeSpace.None;
    }

    /// <summary>
    /// Where to stand in <paramref name="arriving"/>, replacing <paramref name="standing"/>: the same
    /// place where it is still there, by <see cref="ExplorePlace.Carry"/>, and with the root still
    /// open only where the arriving tree is rooted in the same place. A scan of another drive is
    /// another volume, and opens at it.
    /// </summary>
    public ExploreStanding CarriedTo(ExploreTree? standing, ExploreTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        var sameRoot = standing is not null && ExplorePlace.TryCarry(standing, standing.RootNode, arriving) is not null;

        return new ExploreStanding(ExplorePlace.Carry(standing, Node, arriving), RootOpened && sameRoot);
    }
}
