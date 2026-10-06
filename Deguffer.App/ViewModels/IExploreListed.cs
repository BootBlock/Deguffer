namespace Deguffer.App.ViewModels;

/// <summary>
/// A row of the Explore page's list, whichever layout put it there: a child of the folder on screen
/// (<see cref="ExploreRow"/>), or one of the largest files below it (<see cref="ExploreFileRow"/>).
///
/// <para>The page needs only these two facts of a row, and needs them of both kinds: which node it
/// stands for, so the list's selection can be sent to <see cref="ExploreSelection"/> and put back
/// from it, and whether a double click descends or opens. One list serves both layouts so that the
/// rules guarding its selection (§7.1's "never pre-selects") are written once.</para>
/// </summary>
public interface IExploreListed
{
    /// <summary>Which node of the tree on screen this row stands for.</summary>
    int Node { get; }

    bool IsDirectory { get; }
}
