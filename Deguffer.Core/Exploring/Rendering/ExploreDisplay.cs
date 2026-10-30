using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// What the Explore page shows round its picture, from what it is showing: which panels are open,
/// which colours the picture and its legend are in, what is measured for them, and the sentence that
/// names a view the page could not draw as asked.
///
/// <para>One value for all of them because each answers from the same four facts, and a panel that
/// read them for itself is how the legend comes to name a scale the map is not drawn in.</para>
/// </summary>
/// <param name="Tree">The tree on screen, or null before anything has been scanned.</param>
/// <param name="View">The view the reader asked for, which a running scan can substitute (see <see cref="Note"/>).</param>
/// <param name="Colouring">What the colours on the map are to say. Never substituted.</param>
/// <param name="Look">How the maps are drawn, which holds the colours each picture is drawn in.</param>
/// <param name="InContrast">
/// Whether the map is drawn in a high contrast theme's two colours, in which its colours say nothing.
/// </param>
public readonly record struct ExploreDisplay(
    ISizedTree? Tree,
    ExploreView View,
    ExploreColouring Colouring,
    MapLook Look,
    bool InContrast)
{
    /// <summary>
    /// The colours the picture is drawn in, and so the legend beside it: each picture keeps its own,
    /// so a change of view is a change of scheme. See <see cref="MapLook.SchemeFor"/>.
    /// </summary>
    public ExploreScheme Scheme => Look.SchemeFor(View);

    /// <summary>What each colour on the map means. See <see cref="MapLegend.For"/>.</summary>
    public IReadOnlyList<LegendBand> Legend => MapLegend.For(Colouring, Scheme);

    /// <summary>
    /// Whether to show the legend: only where the colours have one, only when there is a picture
    /// rather than a list, only once something has been scanned into it, and not in a high contrast
    /// theme's two colours, which none of the bands is. A scale beside an empty card explains nothing
    /// and reads as part of the empty state.
    /// </summary>
    public bool ShowsLegend =>
        Legend.Count > 0
        && View is not (ExploreView.List or ExploreView.Files)
        && Tree is not null
        && !InContrast;

    /// <summary>Whether to show the Files layout's filters: while it is on screen and has something to filter.</summary>
    public bool ShowsFileFilters => SearchesFiles && Tree is not null;

    /// <summary>
    /// Whether to show the panel of what grew: whenever the colours are growth and something has been
    /// scanned, in the list view as well as the pictures, because the panel is a list of its own and
    /// a reader in the list view has asked the same question. The replay of the comparison follows
    /// the same answer.
    /// </summary>
    public bool ShowsGrowth => Colouring == ExploreColouring.Growth && Tree is not null;

    /// <summary>
    /// Whether to show the panel of what kind of file fills the folder on screen: whenever the colours
    /// are types and something has been scanned, in the lists as well as the pictures, as
    /// <see cref="ShowsGrowth"/> is.
    /// </summary>
    public bool ShowsTypes => MeasuresTypes && Tree is not null;

    /// <summary>
    /// Whether the largest files below the folder on screen are searched for: only while the Files
    /// layout is on screen, because the search is a pass over every file below that folder.
    /// </summary>
    public bool SearchesFiles => View == ExploreView.Files;

    /// <summary>
    /// Whether what kind of file fills each folder is measured: only while the map is coloured by type,
    /// because each measurement is a pass over the folder or the tree.
    /// </summary>
    public bool MeasuresTypes => Colouring == ExploreColouring.Type;

    /// <summary>
    /// Whether the picture on screen can be zoomed, so a zoom is part of where the reader is and the
    /// line naming how the mouse zooms and drags is worth showing. See <see cref="ExploreSurface.Zooms"/>.
    /// Read from the view rather than from the map, because the map hears of a change of view only
    /// once the page has said it.
    /// </summary>
    public bool Zooms => Tree is { } tree && ExploreSurface.Zooms(tree, View);

    /// <summary>
    /// How what is on screen differs from what the View box names, or null when it does not.
    ///
    /// <para>Only ever raised by a partial tree, and every view is affected by one. Its children are
    /// ordered by name rather than by size, so that a scan in progress stays still, and neither the
    /// treemap nor the sunburst can be drawn from that at all — squarification is defined only over
    /// a decreasing sequence, and a sunburst's residual wedge assumes the small children are the
    /// tail — so the map substitutes the icicle on top. A scan reading the file table publishes no
    /// partial tree, so it never says any of this.</para>
    ///
    /// <para>The list gets the sentence as much as the pictures do. It reorders itself alphabetically
    /// and back again, which is a substitution too, and one nobody is told about is the kind a user
    /// reads as a bug.</para>
    ///
    /// <para>The Files layout is ordered by size whatever the tree is, so what it has to say is that
    /// the scan has not reached everything yet: a larger file may still be found.</para>
    /// </summary>
    public string? Note => Tree is { ChildOrder: not ExploreChildOrder.BySize } tree
        ? View switch
        {
            ExploreView.Files =>
                "The largest files found so far. A larger one may still turn up before the scan "
                + "finishes.",
            ExploreView.Treemap when ExploreSurface.Drawn(tree, ExploreView.Treemap) == ExploreView.Icicle =>
                "Drawing the icicle, in name order, while the scan runs. A treemap reorders every "
                + "folder as it grows, so it follows when the scan finishes.",
            ExploreView.Sunburst when ExploreSurface.Drawn(tree, ExploreView.Sunburst) == ExploreView.Icicle =>
                "Drawing the icicle, in name order, while the scan runs. A sunburst turns every "
                + "wedge after one that grows, so it follows when the scan finishes.",
            _ =>
                "In name order while the scan runs, so nothing moves as a folder grows. Largest "
                + "first when the scan finishes.",
        }
        : null;
}
