namespace Deguffer.App.ViewModels;

/// <summary>
/// What one Storage row says about itself in words that do not change while it is on screen: the names
/// its links and its disclosure are announced by, and what its Contents tab says about its steps.
///
/// <para>Apart from <see cref="FindingViewModel"/> because none of it depends on what the row holds
/// ticked or kept. It is fixed by the provider's name, how many steps it planned and whether it is
/// waiting for approved folders, and none of those changes before a preview replaces the row. The row
/// keeps the state that moves; this keeps the sentences that do not.</para>
/// </summary>
/// <param name="name">The row's name, as the provider gives it.</param>
/// <param name="stepCount">Every step the provider planned, kept ones included.</param>
/// <param name="awaitingSourceFolders">Whether the row is waiting for a folder to be approved.</param>
public sealed class FindingRowText(string name, int stepCount, bool awaitingSourceFolders)
{
    /// <summary>What the row's link to its item list says: how many items the list holds.</summary>
    public string ItemsLinkLabel { get; } = stepCount == 1 ? "1 item" : $"{stepCount} items";

    /// <summary>
    /// What a screen reader calls that link. The words on screen are a count, and a count says nothing
    /// about whose items they are.
    /// </summary>
    public string ItemsLinkName { get; } = $"Choose from the items in {name}";

    /// <summary>What the Contents tab says about a row whose steps are listed as items on the Storage page.</summary>
    public string ItemsSentence { get; } = stepCount == 1
        ? "One item, listed on the Storage page with its size and its age."
        : $"{stepCount} items, listed on the Storage page, where each can be chosen on its own.";

    /// <summary>
    /// What the Contents tab holds, named for which of the three things it is.
    ///
    /// A row with nowhere approved to look has left nothing alone: it has not looked. Calling its
    /// guidance "what was left alone" borrows §5.2's protected-path vocabulary for a sentence that
    /// is asking the user for something, and puts the only instruction on the screen behind a label
    /// that reads as a report.
    /// </summary>
    public string DetailHeader { get; } = stepCount > 0
        ? "What this will do"
        : awaitingSourceFolders
            ? "What Deguffer needs"
            : "What was left alone";

    /// <summary>
    /// What a screen reader calls the compact row's disclosure. The whole row is that disclosure's
    /// header there, so it derives no name of its own and would otherwise be announced as an
    /// unnamed button.
    ///
    /// Named for the row rather than for what is inside it, because in the compact view the
    /// disclosure always holds the sentence §7 asks each row to state, whether or not there is a
    /// plan under it, and <see cref="DetailHeader"/> names only the plan half.
    /// </summary>
    public string DetailToggleName { get; } = $"More about {name}";

    /// <summary>
    /// What a screen reader calls this row's information link. Every row's link reads "What is
    /// this?", so without a name of its own a reader hears the same three words down the whole list
    /// with nothing to tell one from another.
    /// </summary>
    public string InformationLinkName { get; } = $"What is {name}?";
}
