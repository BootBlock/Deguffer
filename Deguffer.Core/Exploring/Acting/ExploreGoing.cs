namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What a screen marks as about to go once a removal has said what of the selection it will act on,
/// and only that: a refused item marked as going tells the user the opposite of what will happen
/// (§7.1).
/// </summary>
public static class ExploreGoing
{
    /// <summary>
    /// Each of <paramref name="picked"/> whose item is among <paramref name="going"/>.
    ///
    /// <para><paramref name="items"/> names <paramref name="picked"/> position by position, and
    /// <paramref name="going"/> is part of <paramref name="items"/>, so all of it going means every
    /// pick does. Matched by the item's own equality through one set, because a Shift range or Ctrl+A
    /// picks a whole folder, and this runs on the page's thread before the dialog can appear: a
    /// search of the list for each pick froze the window for most of a second at twenty
    /// thousand.</para>
    ///
    /// <para>Generic because it reads nothing of an item but its equality, which is what lets a test
    /// count the comparisons.</para>
    /// </summary>
    public static IReadOnlyList<int> Picks<TItem>(IReadOnlyList<int> picked, IReadOnlyList<TItem> items, IReadOnlyList<TItem> going)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(going);

        if (going.Count == items.Count)
        {
            return picked;
        }

        var set = going.ToHashSet();

        return [.. picked.Where((_, i) => set.Contains(items[i]))];
    }
}
