using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Deguffer.App.ViewModels;

/// <summary>One row of the list view, and one entry of any other view's detail.</summary>
/// <param name="Node">Which node in the tree this stands for.</param>
/// <param name="Share">
/// How much of the containing directory this accounts for, 0 to 100. The bar is drawn against the
/// parent rather than against the largest sibling, because the question the list answers is "what
/// is this folder made of" and a bar scaled to the biggest child says nothing about that.
/// </param>
/// <param name="AgeLabel">
/// How long ago this was last written, as a sentence — the column the list shows. Relative rather
/// than a date, because the question is "is this still in use", and "2 years ago" answers it where
/// "2024-03-11" makes the reader do the subtraction.
/// </param>
/// <param name="DatesLabel">
/// Both exact dates, for the row's tooltip. The column rounds to the week and the year, which is
/// right for scanning a list and wrong for the one row somebody has stopped on.
/// </param>
public sealed record ExploreRow(
    int Node,
    string Name,
    string SizeLabel,
    double Share,
    bool IsDirectory,
    bool IsLink,
    bool IsApproximate,
    string AgeLabel,
    string DatesLabel)
{
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
