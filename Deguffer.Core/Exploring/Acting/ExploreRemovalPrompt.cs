using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What the user is asked before an Explore removal, and what they are told it costs.
///
/// <para>A Core type for the reason <see cref="Execution.ConfirmationRequirement"/> is one: whether
/// a deletion is reversible, and whether the user has been told so, is a safety decision rather
/// than a matter of dialog layout. §7.1 makes permanent removal "a deliberate second choice that
/// says what it is", and a sentence that only exists inside a WinUI dialog is a sentence nothing
/// can hold to that.</para>
///
/// <para>It states no tier and calls nothing safe. Explore has classified none of this — that is
/// the whole separation §7.1 draws between the two pages — so what it can honestly say is what the
/// removal does and whether it can be undone.</para>
/// </summary>
/// <param name="Title">The question, naming the subject.</param>
/// <param name="Consequence">What happens, and in the irreversible case saying so.</param>
/// <param name="ConfirmLabel">The affirmative button, worded so it is not the same word twice.</param>
public sealed record ExploreRemovalPrompt(string Title, string Consequence, string ConfirmLabel)
{
    public static ExploreRemovalPrompt For(ExploreRemovalMode mode, IReadOnlyList<ExploreItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var subject = items is [{ } only]
            ? $"'{Path.GetFileName(only.Path)}'"
            : $"{items.Count} items";

        var size = FreeSpace.Format(items.Sum(i => i.Bytes));

        return mode == ExploreRemovalMode.RecycleBin
            ? new ExploreRemovalPrompt(
                $"Move {subject} to the Recycle Bin?",

                // The bin's arithmetic said plainly. A user watching a free-space figure would
                // otherwise reasonably expect this to move it, and §8's fourth question is exactly
                // that the Recycle Bin reclaims nothing until it is emptied.
                $"{subject} ({size}) goes to the Recycle Bin, where you can put it back. The drive "
                + "gets the space only once the bin is emptied.",
                "Move to Recycle Bin")
            : new ExploreRemovalPrompt(
                $"Permanently delete {subject}?",
                $"{subject} ({size}) is deleted outright. It does not go to the Recycle Bin and it "
                + "cannot be undone. Deguffer has not classified it, so what it is worth is yours to "
                + "judge.",
                "Delete permanently");
    }
}
