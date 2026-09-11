namespace Deguffer.Core.Choosing;

/// <summary>
/// The rule behind a checkbox that stands for several items: a group's heading, or every item a
/// search is showing.
///
/// <para>Here rather than in the shell so the rule is provable without a window, as
/// <see cref="Execution.ConfirmationRequirement"/> is. The shell decides which items a checkbox covers,
/// and it covers only items on screen: a click must never tick something a search is hiding.</para>
/// </summary>
public static class ItemSelection
{
    /// <summary>
    /// What the checkbox shows: ticked when every item it covers that can be ticked is ticked, clear when
    /// none is, and mixed (null) otherwise.
    ///
    /// <para>An item that cannot be ticked is left out of the question: it has nothing to reclaim, it is
    /// kept, or it needs rights this process does not have. The reader cannot change it, so a heading
    /// that counted it could never reach "ticked", and would be a checkbox that no click can make true.
    /// Where nothing it covers can be ticked, it is clear.</para>
    /// </summary>
    public static bool? StateOf(IEnumerable<(bool Selected, bool CanBeSelected)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var anyTicked = false;
        var anyClear = false;

        foreach (var (selected, canBeSelected) in items)
        {
            if (!canBeSelected)
            {
                continue;
            }

            anyTicked |= selected;
            anyClear |= !selected;
        }

        return (anyTicked, anyClear) switch
        {
            (true, false) => true,
            (true, true) => null,
            _ => false,
        };
    }

    /// <summary>
    /// What one click on that checkbox writes to every item it covers that can be ticked.
    ///
    /// <para>A clear checkbox ticks them. A ticked one clears them, and so does a mixed one: a state
    /// that says "some" gets the answer that deletes less, so a single click never adds to a run items
    /// the reader may not have looked at. Ticking a mixed group then takes two clicks, and each of them
    /// does one plain thing.</para>
    /// </summary>
    public static bool ValueForClick(bool? state) => state is false;
}
