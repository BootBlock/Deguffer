namespace Deguffer.Core.Execution;

/// <summary>
/// What one row of the preview is reporting, as a single value.
///
/// <para>It exists so that the row's own label and any sentence written about the whole set of rows
/// are read off the same answer rather than off two copies of the condition behind it. A second
/// copy is free to disagree with the words on screen, and one did: an info bar deriving its
/// sentence from the byte totals announced that the caches were already clear directly above a row
/// saying it had not been examined.</para>
///
/// <para>The value is decided in the shell rather than here, because the last two states depend on
/// the token the app is running under as well as on what the scan found — see
/// <c>FindingViewModel.Status</c>. The value and the words for it live together in this file so
/// that every reader of one gets the other.</para>
/// </summary>
public enum FindingStatus
{
    /// <summary>
    /// The provider searches only folders the user has approved, and has none. It has something to
    /// ask for rather than something to report, so this is asked before presence: the .NET build
    /// output is present whenever the SDK is, approved folders or not.
    /// </summary>
    AwaitingSourceFolders,

    /// <summary>The tool itself is not on this machine, so there is no location to describe.</summary>
    ToolchainMissing,

    /// <summary>A root Windows would not let Deguffer list. The zero beside it measures nothing.</summary>
    UnreadableRoot,

    /// <summary>
    /// A location Deguffer declined to look at, or could not locate. The zero beside it is about
    /// what was examined, and nothing was.
    /// </summary>
    NotExamined,

    /// <summary>
    /// A cache whose every file is inside the guard on recently changed files: it measures zero and
    /// it is full.
    /// </summary>
    RecentContentHeldBack,

    /// <summary>Examined, and there is genuinely nothing in it.</summary>
    AlreadyClear,

    /// <summary>There is space here, and this process can reclaim it.</summary>
    ReadyToClean,

    /// <summary>
    /// There is space here, and no step of it can be carried out as Deguffer is currently running.
    /// The Windows servicing logs are the whole row of this kind.
    /// </summary>
    NeedsElevation,
}

public static class FindingStatusExtensions
{
    /// <summary>
    /// The two or three words the row states beside its size.
    ///
    /// <para>"Already clear" is a claim about the folder, and the three states above it must not be
    /// reported as that: a folder Windows would not let Deguffer list, a location Deguffer declined
    /// to look at, and a cache held back by the guard window. Each of the three measures zero and
    /// none of them is clear.</para>
    ///
    /// <para>A row that is absent for want of an approved folder needs its own words for the same
    /// reason. Saying "not installed" or "already clear" there names the wrong problem and offers
    /// no way out.</para>
    ///
    /// <para>Length is part of the meaning here, which is why <c>FindingStatusTests</c> holds these
    /// to a ceiling. The standard row draws the label under the size, in a column pinned wide
    /// enough for both, and everything on the row's first line is placed against that column's left
    /// edge. A label too long for it widens the column on that row alone, which walks the "What is
    /// this?" link out of line with every other row in the list.</para>
    /// </summary>
    public static string ToStatusLabel(this FindingStatus status) => status switch
    {
        FindingStatus.AwaitingSourceFolders => "Add a source folder",
        FindingStatus.ToolchainMissing => "Not installed",
        FindingStatus.UnreadableRoot => "Could not be read",
        FindingStatus.NotExamined => "Not examined",
        FindingStatus.RecentContentHeldBack => "Nothing old enough",
        FindingStatus.AlreadyClear => "Already clear",
        FindingStatus.ReadyToClean => "Ready to clean",
        // "Ready to clean" beside a disabled checkbox would contradict itself.
        FindingStatus.NeedsElevation => "Elevate to clean",
        // Throwing rather than falling back on the member's own name, which is what the enum-to-UI
        // extensions elsewhere do. A name is a plausible-looking label, so a state added without
        // words of its own would reach a row reading "RecentContentHeldBack" while every test in
        // FindingStatusTests stayed green — the identifier is non-empty and distinct, which is all
        // they can check. The arm itself is unreachable: FindingViewModel.Status only ever returns
        // a named member, and CS8524 is why it has to be written at all.
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "This status has no words of its own yet."),
    };
}
