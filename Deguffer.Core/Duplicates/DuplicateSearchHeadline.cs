namespace Deguffer.Core.Duplicates;

/// <summary>
/// What the Duplicates page says the search came to: a sentence, drawn over the groups once there
/// are any, and a title of a few words over that sentence in the page's empty state until then.
///
/// <para>The empty state once drew the sentence as its title, with nothing under it, so the page's
/// empty state looked like no other page's. Each outcome is named here once, with both parts, so
/// one cannot be set without the other.</para>
/// </summary>
/// <param name="Title">The empty state's title, shown only while no group is listed.</param>
/// <param name="Sentence">What the search came to, or what to do before one.</param>
public sealed record DuplicateSearchHeadline(string Title, string Sentence)
{
    private const string Arrival = "Each group appears as soon as it is confirmed, in order of the space it could free.";

    /// <summary>Before any search: what to choose.</summary>
    public static readonly DuplicateSearchHeadline NotSearched = new(
        "Nothing searched yet",
        "Choose the drives and folders to search, and what two files must share to be a match.");

    /// <summary>A search has started and confirmed no group yet.</summary>
    public static readonly DuplicateSearchHeadline Searching = new("Searching", Arrival);

    /// <summary>A search running with at least one group confirmed, which the list then shows.</summary>
    public static readonly DuplicateSearchHeadline Arriving = new("Searching", $"Searching. {Arrival}");

    /// <summary>A stop before the files to compare were all found, so nothing could be confirmed.</summary>
    public static readonly DuplicateSearchHeadline StoppedWhileFinding = new(
        "Search stopped",
        "Stopped before the files to compare were all found, so nothing was confirmed.");

    /// <summary>
    /// A search that ran out of memory (#709). The groups confirmed before then stay, and files in
    /// none of them may still have copies.
    /// </summary>
    public static DuplicateSearchHeadline OutOfMemory(bool anyGroup) => new(
        "Search did not finish",
        anyGroup
            ? "The search ran out of memory and did not finish, so files not in a group may still have copies. "
              + "Search fewer locations at once to find them."
            : "The search ran out of memory before it confirmed any duplicates. Search fewer locations at once.");

    /// <summary>A search that ended, or was stopped after the files to compare were found.</summary>
    public static DuplicateSearchHeadline Finished(bool stopped, bool anyGroup)
    {
        var found = anyGroup
            ? "Mark the copies to remove, by hand or with a rule from Mark. Nothing goes until you confirm the list."
            : "No two files in the locations searched matched.";

        return stopped
            ? new("Search stopped", $"Stopped. {found}")
            : new("No duplicates found", found);
    }
}
