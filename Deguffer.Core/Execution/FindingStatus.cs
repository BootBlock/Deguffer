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
/// <para>The value, the rule that decides it and the words for it live together in this file so that
/// every reader of one gets the others. The last two states depend on the token the app is running
/// under as well as on what the scan found, so the rule takes that as an argument rather than reading
/// it: see <see cref="FindingStatusExtensions.ToStatus"/>.</para>
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
    /// A location whose every file is too new for an age limit to let it go: it measures zero and it
    /// is full.
    ///
    /// <para>Two different ages reach here, which is why the label is "Nothing old enough" rather
    /// than anything naming a setting. The first is the user's guard on recently changed files. The
    /// second is a command that takes an age of its own — <c>FhManagew.exe -cleanup &lt;days&gt;</c>,
    /// where the files held back can be a year old and the user's guard may be switched off
    /// entirely. See <see cref="CleanupStep.WithheldRecent"/>.</para>
    /// </summary>
    RecentContentHeldBack,

    /// <summary>
    /// A location whose every byte a previous clean could not take, and which Windows still will not
    /// release: it measures zero and it is full.
    ///
    /// <para>Both kinds of refusal reach here — another program holding files open, and Windows
    /// declining for any other reason — and the label names neither. The plan's notes say which, and
    /// how much, and the label has twenty characters to say it in. See
    /// <see cref="CleanupStep.Refused"/>.</para>
    /// </summary>
    RefusedByWindows,

    /// <summary>
    /// A location whose items are on the user's keep list. It measures zero because the user asked
    /// for that, and it is full.
    /// </summary>
    OnKeepList,

    /// <summary>
    /// A location whose only content is Outlook mail stores, which Deguffer never removes (§9). It
    /// measures zero and it is not empty, and a tool's own command withheld because of one is the
    /// same case: the row has something in it and nothing Deguffer will take.
    /// </summary>
    MailStoresHeldBack,

    /// <summary>
    /// A location an update left behind that Deguffer holds back until the update has finished. It
    /// measures zero and it is full, and it is offered once Windows has restarted and settled.
    /// </summary>
    UpdateInProgress,

    /// <summary>Examined, and there is genuinely nothing in it.</summary>
    AlreadyClear,

    /// <summary>There is space here, and this process can reclaim it.</summary>
    ReadyToClean,

    /// <summary>
    /// There is space here, and no step of it can be carried out as Deguffer is currently running.
    /// The rows under the Windows directory and at the top of the system drive are of this kind.
    /// </summary>
    NeedsElevation,
}

public static class FindingStatusExtensions
{
    /// <summary>
    /// What a row built from <paramref name="offered"/> is reporting, for a process holding the rights
    /// <paramref name="isElevated"/> describes.
    ///
    /// <para>Presence is asked after <see cref="Finding.AwaitingSourceFolders"/>, because the two do
    /// not line up: the .NET build output is present whenever the SDK is, approved folders or not,
    /// and that row has as little to report as the four that are absent for the same reason.</para>
    ///
    /// <para>The held-back state asks the plan what the measurement actually withheld, never whether
    /// a guard is switched on. Driving the real window settled that: with the guard at seven days,
    /// deriving it from the setting put "Nothing old enough" on twelve rows, most of them simply
    /// empty — the same false claim wearing the opposite costume.</para>
    ///
    /// <para>A row with something to remove is ready only where a step of it can be ticked, which is
    /// <see cref="Choosing.StepChoice.AnyCanBeSelected"/>'s question, so the label and the checkbox
    /// beside it cannot disagree.</para>
    /// </summary>
    /// <param name="offered">
    /// The finding with the keep list applied, which is what the row describes. See
    /// <see cref="CleanupPlan.WithKeepList"/>.
    /// </param>
    public static FindingStatus ToStatus(this Finding offered, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.AwaitingSourceFolders)
        {
            return FindingStatus.AwaitingSourceFolders;
        }

        if (!offered.IsPresent)
        {
            return FindingStatus.ToolchainMissing;
        }

        if (offered.HasSomethingToRemove)
        {
            return Choosing.StepChoice.AnyCanBeSelected(offered, isElevated)
                ? FindingStatus.ReadyToClean
                : FindingStatus.NeedsElevation;
        }

        return offered.Plan switch
        {
            { HasUnreadableRoot: true } => FindingStatus.UnreadableRoot,
            { WasNotExamined: true } => FindingStatus.NotExamined,
            { HasRecentContentHeldBack: true } => FindingStatus.RecentContentHeldBack,
            { HasRefusedContent: true } => FindingStatus.RefusedByWindows,
            { HoldsKeepListItems: true } => FindingStatus.OnKeepList,
            { HoldsMailStores: true } => FindingStatus.MailStoresHeldBack,
            { WaitsForAnUpdate: true } => FindingStatus.UpdateInProgress,
            _ => FindingStatus.AlreadyClear,
        };
    }

    /// <summary>
    /// The two or three words the row states beside its size.
    ///
    /// <para>"Already clear" is a claim about the folder, and the seven states above it must not be
    /// reported as that: a folder Windows would not let Deguffer list, a location Deguffer declined
    /// to look at, a cache held back by the guard window, a folder whose contents Windows would not
    /// let a clean take, a location holding what the user keeps, a location holding Outlook data
    /// files, and a location an unfinished update is holding back. Each of the seven measures zero
    /// and none of them is clear.</para>
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
        FindingStatus.RefusedByWindows => "Refused by Windows",
        FindingStatus.OnKeepList => "On your keep list",
        FindingStatus.MailStoresHeldBack => "Outlook data kept",
        FindingStatus.UpdateInProgress => "Update in progress",
        FindingStatus.AlreadyClear => "Already clear",
        FindingStatus.ReadyToClean => "Ready to clean",
        // "Ready to clean" beside a disabled checkbox would contradict itself.
        FindingStatus.NeedsElevation => "Elevate to clean",
        // Throwing rather than falling back on the member's own name, which is what the enum-to-UI
        // extensions elsewhere do. A name is a plausible-looking label, so a state added without
        // words of its own would reach a row reading "RecentContentHeldBack" while every test in
        // FindingStatusTests stayed green — the identifier is non-empty and distinct, which is all
        // they can check. The arm itself is unreachable: ToStatus only ever returns a named member,
        // and CS8524 is why it has to be written at all.
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "This status has no words of its own yet."),
    };
}
