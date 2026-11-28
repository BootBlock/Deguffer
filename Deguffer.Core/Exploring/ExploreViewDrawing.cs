using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring;

/// <summary>What each <see cref="ExploreView"/> puts on screen.</summary>
public static class ExploreViewDrawing
{
    /// <summary>
    /// Whether <paramref name="view"/> draws a picture of the contents rather than listing them. A
    /// picture is read as a whole, so a larger window gives it more to show; a list and the tree are
    /// read a row at a time, so they keep to the width a row can be read across.
    /// </summary>
    public static bool DrawsPicture(this ExploreView view) =>
        view is ExploreView.Treemap or ExploreView.Icicle or ExploreView.Sunburst;

    /// <summary>
    /// The name <paramref name="view"/> goes by wherever a page offers it, so the Explore and Memory
    /// pages call each layout the same thing.
    /// </summary>
    public static string DisplayName(this ExploreView view) => view switch
    {
        ExploreView.Treemap => "Treemap",
        ExploreView.Icicle => "Icicle",
        ExploreView.Sunburst => "Sunburst",
        ExploreView.List => "List",
        ExploreView.Tree => "Tree",
        ExploreView.Files => "Files",
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "No layout has this number."),
    };
}
