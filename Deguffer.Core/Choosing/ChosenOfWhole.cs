using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Core.Text;

namespace Deguffer.Core.Choosing;

/// <summary>
/// What a Storage row states about its size and its items, given how much of it is chosen.
///
/// <para>A row that takes part of what it holds states that part against the whole: "1.2 GB of 18.4 GB",
/// "2 of 40 items". Its figure is then what a clean takes, and the whole beside it is what the row
/// holds, which a row whose checkbox reads as full can still exceed: a step needing administrator
/// rights counts towards the whole and cannot be ticked. A row that takes all of it, or none, states
/// the whole.</para>
/// </summary>
public static class ChosenOfWhole
{
    /// <summary>
    /// Whether the row takes part of its whole: some step the whole counts is ticked and another is not.
    ///
    /// <para>The whole counts every step its plan offers that removes something, ticked or not and
    /// whether or not it can be ticked, so this asks the question of each of those. A kept item is not
    /// in the plan, and a step that removes nothing adds nothing to the whole, so neither can make the
    /// row's figure a part.</para>
    /// </summary>
    public static bool TakesPart(IEnumerable<(CleanupStep Step, bool Selected, bool IsKept)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return ItemSelection.StateOf(steps.Select(s => (s.Selected, !s.IsKept && s.Step.RemovesSomething))) is null;
    }

    /// <summary>The row's figure: the chosen part of the whole where the row takes part of it, otherwise the whole.</summary>
    /// <param name="takesPart">What <see cref="TakesPart"/> answers for the row.</param>
    /// <param name="chosen">What the ticked steps come to.</param>
    /// <param name="whole">What every step the row's plan offers comes to.</param>
    public static string Size(bool takesPart, ScanSize chosen, ScanSize whole) => takesPart
        ? $"{FreeSpace.Format(chosen)} of {FreeSpace.Format(whole)}"
        : FreeSpace.Format(whole);

    /// <summary>
    /// The row's link to its item list: how many items it lists, and how many of them are ticked where
    /// some are and some are not.
    /// </summary>
    /// <param name="chosen">How many of the row's items are ticked.</param>
    /// <param name="total">Every item the row lists, kept ones included.</param>
    public static string Items(int chosen, int total) => chosen > 0 && chosen < total
        ? $"{chosen:N0} of {Counted.Of(total, "item", "items")}"
        : Counted.Of(total, "item", "items");
}
