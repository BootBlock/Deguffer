namespace Deguffer.Core.Exploring;

/// <summary>
/// How an <see cref="ExploreTree"/> orders each node's children.
///
/// <para>A property of the tree rather than a preference of whatever draws it, because one of the
/// layouts cannot be handed the wrong one. Squarification is defined only over a decreasing
/// sequence, so <see cref="Layout.TreemapLayout"/> requires <see cref="BySize"/> and refuses
/// anything else rather than packing rows whose shapes would mean nothing.</para>
/// </summary>
public enum ExploreChildOrder
{
    /// <summary>
    /// Largest first. What a finished scan wants, and what every consumer reads: the treemap
    /// requires it, the icicle draws its aggregate from it, and the list shows it directly.
    /// </summary>
    BySize = 0,

    /// <summary>
    /// By name, compared ordinally and without case, ties broken by node number.
    ///
    /// <para>What a scan still in progress wants, for a measured reason. Bederson, Shneiderman and
    /// Wattenberg (<i>ACM Transactions on Graphics</i> 21(4), 2002, pp. 833-854) score layout change
    /// across 100 items and put a squarified treemap at 14.82 against slice-and-dice's 0.25:
    /// re-sorting siblings by a size that is still growing rearranges the whole picture on every
    /// snapshot. A name does not grow, so under this order a child only ever grows in place.</para>
    ///
    /// <para>Ordinal rather than culture-aware, so the same disk draws the same picture on every
    /// machine. The tie-break is there because the sort underneath is not stable, and two siblings
    /// can differ only in case.</para>
    /// </summary>
    ByName = 1,
}
