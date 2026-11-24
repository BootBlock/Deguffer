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
}
