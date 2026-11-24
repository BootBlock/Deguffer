using Deguffer.Core.Scanning;
using Deguffer.Core.Text;

namespace Deguffer.Core.Choosing;

/// <summary>
/// What a Storage row states about its size and its items, given how much of it is chosen.
///
/// <para>A row whose checkbox is mixed (<see cref="ItemSelection.StateOf"/> answers null) states the
/// part that is chosen against the whole: "1.2 GB of 18.4 GB", "2 of 40 items". Stating only the whole
/// beside it put a figure on the row that the clean would not reach, and a mixed row read the same as
/// a full one. A ticked or clear row states the whole, which is what it would take or what it offers.</para>
/// </summary>
public static class ChosenOfWhole
{
    /// <summary>The row's figure: the whole, or the chosen part of it where the row is mixed.</summary>
    /// <param name="state">The row checkbox's state, as <see cref="ItemSelection.StateOf"/> gives it.</param>
    /// <param name="chosen">What the ticked steps come to.</param>
    /// <param name="whole">What every step the row offers comes to.</param>
    public static string Size(bool? state, ScanSize chosen, ScanSize whole) => state is null
        ? $"{FreeSpace.Format(chosen)} of {FreeSpace.Format(whole)}"
        : FreeSpace.Format(whole);

    /// <summary>The row's link to its item list: how many items it holds, and how many are chosen where the row is mixed.</summary>
    /// <param name="state">The row checkbox's state, as <see cref="ItemSelection.StateOf"/> gives it.</param>
    /// <param name="chosen">How many of the row's items are ticked.</param>
    /// <param name="total">Every item the row lists, kept ones included.</param>
    public static string Items(bool? state, int chosen, int total) => state is null
        ? $"{chosen:N0} of {Counted.Of(total, "item", "items")}"
        : Counted.Of(total, "item", "items");
}
