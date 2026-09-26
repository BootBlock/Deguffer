namespace Deguffer.Core.Exploring;

/// <summary>
/// What of the Explore page survives a redraw: whether the selection is carried into the arriving
/// tree or emptied, and whether the rows are brought up to date in place or rebuilt.
///
/// <para>Both turn on what the replacement meant, which the page cannot see from the tree alone. A
/// snapshot landing mid-scan measures again what the page is already standing in; stepping into a
/// folder is a new subject; and a scan that came back rooted somewhere else is a different
/// picture.</para>
/// </summary>
/// <param name="KeepsSelection">
/// Whether what is picked carries into the arriving tree, where it still names what it named. A
/// step starts with nothing picked, because §7.1 lets Explore act only on what the user picked out
/// by hand, and a selection made in one folder is not a selection in the next. A snapshot keeps it,
/// because dropping the selection every time one lands is what made a folder impossible to pick
/// while a scan ran.
/// </param>
/// <param name="KeepsRows">
/// Whether the rows on screen are the same list, so they are updated in place and the reader keeps
/// their place in it. See <see cref="Between"/>.
/// </param>
public readonly record struct ExploreRedraw(bool KeepsSelection, bool KeepsRows)
{
    /// <summary>
    /// How moving from <paramref name="from"/> in <paramref name="standing"/> to
    /// <paramref name="to"/> in <paramref name="arriving"/> relates to what is on screen.
    ///
    /// <para><b>The same directory</b> is the one <see cref="ExplorePlace.TryCarry"/> finds where the
    /// page was standing. <b>Continuing</b> is that, and not a step: opening the root, or going back
    /// out of it onto the volume, is the same directory and the same rows, but it is a step like
    /// opening any folder — and the first click of the double-click that opened the root picked the
    /// root.</para>
    ///
    /// <para><b>The rows are kept</b> only while the list is the same list: the same directory, in
    /// the same order. A finished tree arrives with its children in size order after a walk's
    /// snapshots delivered them in name order, and that is a different list rather than this one
    /// changed — every row has moved, so reconciling it would be a move per entry, and the scroll
    /// position it would preserve is a position in content that is no longer there.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing is.</param>
    public static ExploreRedraw Between(
        ExploreTree? standing, ExplorePosition from, ExploreTree arriving, ExplorePosition to)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        var sameDirectory = ExplorePlace.TryCarry(standing, from.Node, arriving) == to.Node;

        var continuing = sameDirectory
            && (!ReferenceEquals(standing, arriving) || to.OnVolume == from.OnVolume);

        return new ExploreRedraw(
            KeepsSelection: continuing,
            KeepsRows: sameDirectory && standing?.ChildOrder == arriving.ChildOrder);
    }
}
