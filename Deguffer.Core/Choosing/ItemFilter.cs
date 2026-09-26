using Deguffer.Core.Execution;

namespace Deguffer.Core.Choosing;

/// <summary>
/// What the search box above a list of items asks: which of them to show.
///
/// <para>Every word typed has to appear somewhere in the item, in any order and regardless of case,
/// so "api 2024" narrows to an item whose project is named for the one and whose path holds the other.
/// It asks what the reader can see: the description, which carries the path, each facet's value, and
/// the heading the item is listed under. A facet's label is not searched, because every item carries
/// the same labels and a word from one would match them all.</para>
///
/// <para><b>It decides what is shown, never what is ticked.</b> A tick the search hides still goes in
/// the run. <see cref="HiddenSelected"/> is what lets the list say so, rather than let a hidden item be
/// deleted in a run the reader believes they narrowed.</para>
/// </summary>
public sealed class ItemFilter
{
    private readonly string[] _words;

    public ItemFilter(string? query) =>
        _words = query?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>Whether nothing was typed, so every item shows.</summary>
    public bool IsEmpty => _words.Length == 0;

    public bool Matches(CleanupStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        foreach (var word in _words)
        {
            if (!Mentions(step, word))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// How many ticked items this search hides. Every one of them is still in the run, which is exactly
    /// what a reader looking at the shorter list cannot see for themselves.
    /// </summary>
    public int HiddenSelected(IEnumerable<(CleanupStep Step, bool Selected)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return IsEmpty ? 0 : items.Count(item => item.Selected && !Matches(item.Step));
    }

    /// <summary>
    /// <paramref name="groups"/> with only the items this search shows, in the same order. A group left
    /// with nothing to show is dropped, because a heading over an empty list reads as a result.
    /// </summary>
    public IReadOnlyList<ItemGroup<T>> Showing<T>(IReadOnlyList<ItemGroup<T>> groups, Func<T, CleanupStep> stepOf)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(stepOf);

        if (IsEmpty)
        {
            return groups;
        }

        List<ItemGroup<T>> shown = [];

        foreach (var group in groups)
        {
            List<T> items = [.. group.Items.Where(item => Matches(stepOf(item)))];

            if (items.Count > 0)
            {
                shown.Add(group with { Items = items });
            }
        }

        return shown;
    }

    private static bool Mentions(CleanupStep step, string word)
    {
        if (Contains(step.Description, word))
        {
            return true;
        }

        return Contains(step.Group, word) || step.Facets.Any(facet => Contains(facet.Value, word));
    }

    private static bool Contains(string? text, string word) =>
        text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
