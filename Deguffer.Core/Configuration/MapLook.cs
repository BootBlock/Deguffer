namespace Deguffer.Core.Configuration;

/// <summary>
/// How the maps are drawn: the colours each picture is drawn in, and how much room a treemap leaves
/// round what each folder holds. The part of <see cref="AppPreferences"/> the map appearance window
/// changes, as one value.
///
/// <para>A value of its own because the window applies a change before it is written. A reader
/// trying colours wants each one on the map as they pick it, and a preferences file that could not
/// be written is no reason to show them the old one — the order the Explore page's own View and
/// Colour boxes already take, for the same reason.</para>
/// </summary>
/// <param name="Spacing">How much room the treemap leaves round what each folder holds.</param>
/// <param name="Treemap">The colours the treemap is drawn in.</param>
/// <param name="Icicle">The colours the icicle is drawn in.</param>
/// <param name="Sunburst">The colours the sunburst is drawn in.</param>
public sealed record MapLook(
    ExploreSpacing Spacing,
    ExploreScheme Treemap,
    ExploreScheme Icicle,
    ExploreScheme Sunburst)
{
    /// <summary>
    /// The look <paramref name="preferences"/> ask for.
    ///
    /// <para>A value no member names is read as the default. The preferences file is read with
    /// numbers allowed for an enum, so a hand-edited <c>"TreemapScheme": 9</c> arrives as a scheme
    /// that indexes past every palette, and the map would throw on each paint.
    /// <see cref="PreferenceStore.Load"/> holds that a stray value in a cosmetic setting must never
    /// stop the app working.</para>
    /// </summary>
    public static MapLook From(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        return new(
            Defined(preferences.TreemapSpacing, ExploreSpacing.Comfortable),
            Defined(preferences.TreemapScheme, ExploreScheme.Standard),
            Defined(preferences.IcicleScheme, ExploreScheme.Standard),
            Defined(preferences.SunburstScheme, ExploreScheme.Standard));
    }

    /// <summary>
    /// The picture <paramref name="view"/> puts on screen: itself for the three pictures, and the
    /// treemap for the list and the tree. Neither of those draws one, and the map behind them is left
    /// holding the treemap's drawing — so that is the one a scheme chosen there changes.
    /// </summary>
    public static ExploreView Drawn(ExploreView view) =>
        view is ExploreView.Icicle or ExploreView.Sunburst ? view : ExploreView.Treemap;

    /// <summary>The colours <paramref name="view"/> is drawn in. See <see cref="Drawn"/>.</summary>
    public ExploreScheme SchemeFor(ExploreView view) => Drawn(view) switch
    {
        ExploreView.Icicle => Icicle,
        ExploreView.Sunburst => Sunburst,
        _ => Treemap,
    };

    /// <summary>This look with <paramref name="view"/> drawn in <paramref name="scheme"/>. See <see cref="Drawn"/>.</summary>
    public MapLook WithScheme(ExploreView view, ExploreScheme scheme) => Drawn(view) switch
    {
        ExploreView.Icicle => this with { Icicle = scheme },
        ExploreView.Sunburst => this with { Sunburst = scheme },
        _ => this with { Treemap = scheme },
    };

    private static T Defined<T>(T value, T fallback)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;

    /// <summary><paramref name="preferences"/> with this look in place of theirs, and nothing else changed.</summary>
    public AppPreferences Into(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        return preferences with
        {
            TreemapSpacing = Spacing,
            TreemapScheme = Treemap,
            IcicleScheme = Icicle,
            SunburstScheme = Sunburst,
        };
    }
}
