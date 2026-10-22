namespace Deguffer.Core.Configuration;

/// <summary>
/// What the Explore page opens on: the picture it draws, and what that picture's colours say. The
/// part of <see cref="AppPreferences"/> the page's View and Colour boxes change, as one value.
/// </summary>
/// <param name="View">The picture the page draws. Always one of <see cref="Views"/>.</param>
/// <param name="Colouring">What the colours in that picture say.</param>
public sealed record ExploreChoice(ExploreView View, ExploreColouring Colouring)
{
    /// <summary>
    /// The views the Explore page offers, in the order its View box lists them. The tree is the
    /// Memory page's alone, so this and <see cref="ExploreView"/> do not line up by number.
    /// </summary>
    public static IReadOnlyList<ExploreView> Views { get; } =
        [ExploreView.Treemap, ExploreView.Icicle, ExploreView.Sunburst, ExploreView.List, ExploreView.Files];

    /// <summary>
    /// Where <paramref name="view"/> sits in <see cref="Views"/>, or the treemap's place where it is
    /// not there: the tree, or a number no member names.
    /// </summary>
    public static int IndexOf(ExploreView view)
    {
        for (var i = 0; i < Views.Count; i++)
        {
            if (Views[i] == view)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The choice <paramref name="preferences"/> ask for.
    ///
    /// <para>A view the page does not offer, and a colouring no member names, are read as the
    /// defaults. The preferences file is read with numbers allowed for an enum, so a hand-edited
    /// <c>"ExploreColours": 9</c> arrives as a colouring past every item in the Colour box, and
    /// selecting it there throws as the page is built. <see cref="PreferenceStore.Load"/> holds that a
    /// stray value in a cosmetic setting must never stop the app working.</para>
    /// </summary>
    public static ExploreChoice From(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        return new(
            Views[IndexOf(preferences.Explore)],
            StoredEnum.Defined(preferences.ExploreColours, ExploreColouring.Branch));
    }
}
