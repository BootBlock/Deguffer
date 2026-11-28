using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One item of a Layout box: a layout, its name and its icon. The Explore and Memory pages read their
/// items from here, so each layout looks the same on both.
/// </summary>
/// <param name="View">The layout.</param>
/// <param name="Name">What the layout is called. See <see cref="ExploreViewDrawing.DisplayName"/>.</param>
/// <param name="Glyph">The Segoe Fluent Icons glyph drawn beside the name.</param>
public sealed record MapLayoutOption(ExploreView View, string Name, string Glyph)
{
    /// <summary>The Explore page's layouts, in the order of <see cref="ExploreChoice.Views"/>.</summary>
    public static IReadOnlyList<MapLayoutOption> Explore { get; } = For(ExploreChoice.Views);

    /// <summary>The Memory page's layouts, in the order of <see cref="MemoryChoice.Views"/>.</summary>
    public static IReadOnlyList<MapLayoutOption> Memory { get; } = For(MemoryChoice.Views);

    /// <summary>The name, which is what a screen reader says of an item in the box.</summary>
    public override string ToString() => Name;

    private static MapLayoutOption[] For(IReadOnlyList<ExploreView> views) =>
        [.. views.Select(view => new MapLayoutOption(view, view.DisplayName(), GlyphOf(view)))];

    // By code point rather than pasted: a private-use glyph reads back as nothing through a file tool.
    private static string GlyphOf(ExploreView view) => view switch
    {
        ExploreView.Treemap => "\uE80A",
        ExploreView.Icicle => "\uE9D2",
        ExploreView.Sunburst => "\uEB05",
        ExploreView.List => "\uE8FD",
        ExploreView.Tree => "\uF168",
        ExploreView.Files => "\uE7C3",
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "No layout has this number."),
    };
}
