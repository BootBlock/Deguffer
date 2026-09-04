using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row of the list view, and one entry of any other view's detail.
///
/// <para>Split in two by what a later tree can change. The node, the name and what kind of entry
/// it is are the row's identity and are fixed for its life; the size, the share, the age and the
/// dates are re-read from every tree that arrives. That split is what lets a snapshot landing
/// mid-scan update the list in place instead of rebuilding it — a rebuild is a Reset for the bound
/// <c>ListView</c>, which throws away the scroll position and the selection with it.</para>
/// </summary>
public sealed partial class ExploreRow : ObservableObject
{
    /// <param name="parentTotal">
    /// Bytes in the containing directory, which <see cref="Share"/> is measured against.
    /// </param>
    /// <param name="now">
    /// The instant every age in one list is measured from. Passed in rather than read here, so two
    /// rows a millisecond apart cannot land in different days.
    /// </param>
    public ExploreRow(ExploreTree tree, int node, long parentTotal, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Node = node;
        Name = tree.NameOf(node);
        IsDirectory = tree.IsDirectory(node);
        IsLink = tree.IsLink(node);

        SizeLabel = ExploreRowText.Size(tree, node);
        Share = ShareOf(tree, node, parentTotal);
        IsApproximate = tree.HasUnknownSizeBelow(node);
        AgeLabel = ExploreRowText.Age(tree, node, now);
        DatesLabel = ExploreRowText.Dates(tree, node);
    }

    /// <summary>Which node in the tree this stands for.</summary>
    public int Node { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public bool IsLink { get; }

    /// <summary>
    /// How much of the containing directory this accounts for, 0 to 100. The bar is drawn against
    /// the parent rather than against the largest sibling, because the question the list answers is
    /// "what is this folder made of" and a bar scaled to the biggest child says nothing about that.
    /// </summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial string SizeLabel { get; set; }

    /// <summary>Whether the scan could not read everything below this row. See <see cref="Description"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial bool IsApproximate { get; set; }

    /// <summary>
    /// How long ago this was last written, as a sentence — the column the list shows. Relative
    /// rather than a date, because the question is "is this still in use", and "2 years ago" answers
    /// it where "2024-03-11" makes the reader do the subtraction.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    public partial string AgeLabel { get; set; }

    /// <summary>
    /// Both exact dates, for the row's tooltip. The column rounds to the week and the year, which
    /// is right for scanning a list and wrong for the one row somebody has stopped on.
    /// </summary>
    [ObservableProperty]
    public partial string DatesLabel { get; set; }

    /// <summary>
    /// A folder, a link or a file. Segoe Fluent Icons, and never the only thing carrying the
    /// distinction — the row is named for a screen reader by <see cref="Description"/>, which says
    /// it in words.
    /// </summary>
    public string Icon => (IsLink, IsDirectory) switch
    {
        // Written as escapes rather than as the characters themselves: these are private-use
        // codepoints, so pasted into a source file they are indistinguishable from each other, and
        // from nothing at all, in most tooling.
        (true, _) => "\uE71B",   // Link
        (_, true) => "\uE8B7",   // Folder
        _ => "\uE7C3",           // Page
    };

    /// <summary>
    /// What a screen reader should say instead of reading a bar graphic.
    ///
    /// <para>The age is in it because it is a column, and a column read as nothing is a column
    /// somebody cannot use. Core's <c>RelativeAge</c> already renders an absent date as "Unknown" in
    /// words rather than as a blank, which is what makes it safe to read out.</para>
    ///
    /// <para>A refusal is stated once, as a cause, rather than as the bare "at least" that used to
    /// follow the size. That word was unambiguous while it qualified the only figure on the row;
    /// with a second figure beside it, and one whose uncertainty runs the other way, naming what
    /// actually happened is the only reading that stays true of both.</para>
    /// </summary>
    public string Description =>
        $"{Name}, {SizeLabel}{(IsLink ? ", a link" : string.Empty)}, last written {AgeLabel}"
        + (IsApproximate ? ", and some of this could not be read" : string.Empty);

    /// <summary>
    /// Whether this row still stands for <paramref name="node"/> of <paramref name="tree"/>.
    ///
    /// <para>The node number alone is not enough. Through the snapshots of one walk it is, because
    /// they come from one builder whose lists only grow — but a rescan numbers its nodes afresh, and
    /// a row updated on the strength of a coincidence would show one directory's size under
    /// another's name. The name settles it: the parent is the same directory in both trees, or the
    /// page would not be standing on it, so a child of that parent with this number and this name is
    /// this entry.</para>
    /// </summary>
    public bool Is(ExploreTree tree, int node) =>
        Node == node
        && IsDirectory == tree.IsDirectory(node)
        && IsLink == tree.IsLink(node)
        && string.Equals(Name, tree.NameOf(node), StringComparison.Ordinal);

    /// <summary>
    /// Re-read everything a later tree can have changed. See the class summary for why this is not
    /// a new row.
    /// </summary>
    public void Describe(ExploreTree tree, long parentTotal, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(tree);

        SizeLabel = ExploreRowText.Size(tree, Node);
        Share = ShareOf(tree, Node, parentTotal);
        IsApproximate = tree.HasUnknownSizeBelow(Node);
        AgeLabel = ExploreRowText.Age(tree, Node, now);
        DatesLabel = ExploreRowText.Dates(tree, Node);
    }

    private static double ShareOf(ExploreTree tree, int node, long parentTotal) =>
        parentTotal > 0 ? 100.0 * tree.SizeOf(node) / parentTotal : 0;
}

/// <summary>One step of the path back to the root.</summary>
public sealed record ExploreCrumb(int Node, string Name);

/// <summary>One band of the age legend, ready to draw.</summary>
/// <param name="Swatch">
/// The band's colour as a brush. Converted here rather than in Core, which deliberately knows
/// nothing about the UI framework so that the whole of the drawing stays testable without a window.
/// </param>
public sealed record ExploreLegendBand(string Label, SolidColorBrush Swatch)
{
    /// <summary>
    /// Every band, newest first, ready to bind. Built once for the life of the app rather than per
    /// view-model: the scale does not depend on what was scanned, and a brush per band per page
    /// would be objects created to say the same thing again (G5).
    /// </summary>
    public static IReadOnlyList<ExploreLegendBand> All { get; } =
    [
        .. AgePalette.Bands.Select(band => new ExploreLegendBand(
            band.Label,
            new SolidColorBrush(Color.FromArgb(255, band.Colour.Red, band.Colour.Green, band.Colour.Blue)))),
    ];
}
