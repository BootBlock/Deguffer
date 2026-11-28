namespace Deguffer.Core.Configuration;

/// <summary>
/// The layouts the Memory page offers, in the order its Layout box lists them. The Files layout is
/// Explore's alone, so this and <see cref="ExploreView"/> do not line up by number.
/// </summary>
public static class MemoryChoice
{
    /// <summary>The layouts in the order the box lists them.</summary>
    public static IReadOnlyList<ExploreView> Views { get; } =
        [ExploreView.Treemap, ExploreView.Icicle, ExploreView.Sunburst, ExploreView.List, ExploreView.Tree];

    /// <summary>
    /// Where <paramref name="view"/> sits in <see cref="Views"/>, or the treemap's place where it is
    /// not there: the Files layout, or a number no member names.
    /// </summary>
    public static int IndexOf(ExploreView view) => StoredEnum.IndexOf(view, Views);
}
