namespace Deguffer.Core.Viewing;

/// <summary>
/// Which of the Storage page's controls are drawn (§7). A first-time user meets the page before
/// anything has been scanned, and each control that does nothing until there are rows is one more to
/// read past to find the one that matters. So until a scan has produced a row, the page shows the
/// scan alone, in the card the rows will fill.
/// </summary>
/// <param name="ListControls">
/// Whether the bar over the list is drawn: its filters, its view, its order and the menu that ticks
/// every row. From the first row a scan on the page produced, for as long as the page lives, so a
/// rescan that empties the list to fill it again does not move the list down and back up under the
/// pointer, and a filter that hides every row can still be switched back off.
/// </param>
/// <param name="ActionBar">
/// Whether the bar under the list is drawn: Scan, Elevate and scan, what follows a clean, and Clean.
/// From the same first row, and while any work runs before it, because Cancel takes Scan's place
/// there.
/// </param>
public readonly record struct StorageControls(bool ListControls, bool ActionBar)
{
    /// <param name="rowsProduced">Whether any scan on the page has produced a row, drawn or not.</param>
    /// <param name="busy">Whether a scan or a clean is running.</param>
    public static StorageControls For(bool rowsProduced, bool busy) => new(rowsProduced, rowsProduced || busy);

    /// <summary>
    /// Whether the empty card carries Scan. Wherever the bar under the list does not, so the page
    /// shows exactly one Scan, and so one accent, in every state.
    /// </summary>
    public bool EmptyStateOffersScan => !ActionBar;
}
