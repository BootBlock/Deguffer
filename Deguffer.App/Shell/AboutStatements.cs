using Deguffer.Core.Safety;

namespace Deguffer.App.Shell;

/// <summary>
/// What the About page says about the application: what each page is for, what the safety tiers
/// are called and mean, and what each page changes and whether that can be undone. Kept apart from
/// the page so a test can hold every sentence to what the pages really do, which a WinUI page
/// cannot be built in a test process to show.
/// </summary>
public static class AboutStatements
{
    /// <summary>The line under the application's name.</summary>
    public const string Summary =
        "Deguffer shows what is using the space, the memory and the start-up time on this PC, says " +
        "what each thing is, and tells you what each one costs to lose before you agree to lose it.";

    /// <summary>The heading of the card that says what each page is for.</summary>
    public const string PurposesHeading = "What each page is for";

    /// <summary>The line that opens the tiers' card.</summary>
    public const string TiersIntroduction =
        "Every location Storage offers is classified before it is offered. Sizes are easy to " +
        "compute; this classification is the part that takes knowledge, and it is what the tool is for.";

    /// <summary>The heading of the card that says, page by page, what can be undone.</summary>
    public const string SafeguardsHeading = "What each page changes, and what it can undo";

    /// <summary>
    /// Every page but About itself, each with the sentence its rail item states
    /// (<see cref="DestinationPurposes"/>), in the rail's order.
    /// </summary>
    public static readonly IReadOnlyList<PageStatement> Purposes =
    [
        .. Enum.GetValues<Destination>()
            .Where(destination => destination != Destination.About)
            .Select(destination => new PageStatement(
                DestinationNames.Of(destination),
                [DestinationPurposes.Of(destination) + "."])),
    ];

    /// <summary>
    /// §3's four tiers, named as a Storage row's badge and its dialogs name them, and explained in the
    /// words the badge explains itself with.
    /// </summary>
    public static readonly IReadOnlyList<TierStatement> Tiers =
    [
        .. Enum.GetValues<SafetyTier>()
            .Select(tier => new TierStatement(tier, tier.ToDisplayName(), tier.ToExplanation())),
    ];

    /// <summary>
    /// What each page that acts on the machine changes, and how, or whether, it is undone, in the
    /// rail's order. Said page by page because the pages differ: §8 rules out an undo for Storage's
    /// clean, §7.1 puts Explore's and Duplicates' removals in the Recycle Bin, §7.3 backs an entry up
    /// before removing it, §7.5's Disable is undone by Enable, and §7.2.1 and §7.6 change nothing
    /// that needs undoing.
    /// </summary>
    public static readonly IReadOnlyList<PageStatement> Safeguards =
    [
        new(DestinationNames.Of(Destination.Storage),
        [
            "Nothing is cleaned until a scan has shown you what it is, and the exact steps are inspectable on every row before you agree to them.",
            "It asks a tool to clear its own cache wherever the tool offers that, rather than deleting the folder behind its back.",
            "It never targets a tool's root directory, only the children it recognises, because configuration and credentials routinely sit beside the cache.",
            "After every clean it checks that the paths it promised to protect are still there, and says so on the page beside the space the clean freed, either way.",
            "A clean cannot be undone. What you agree to is removed outright rather than moved anywhere first, so it is gone.",
        ]),
        new(DestinationNames.Of(Destination.Explore),
        [
            "Measuring a drive changes nothing on it.",
            "Delete moves what you picked to the Recycle Bin, where you can put it back. Only Delete permanently removes it outright, and it says so before it does.",
        ]),
        new(DestinationNames.Of(Destination.Duplicates),
        [
            "Searching changes nothing, and every group keeps at least one copy.",
            "Move marked to Recycle Bin moves the copies you marked to the Recycle Bin, where you can put them back. Only Delete permanently removes them outright.",
        ]),
        new(DestinationNames.Of(Destination.Health),
        [
            "It only reads what each drive reports, and sends a drive nothing that could change it.",
        ]),
        new(DestinationNames.Of(Destination.InstalledApps),
        [
            "Removing an entry deletes only its registry key: nothing is uninstalled and no file is touched.",
            "Each entry is backed up first unless you turn that off, and you can restore a backup from the page's Backups tab.",
            "Uninstall runs the program's own uninstaller, which is the program's to undo or not.",
        ]),
        new(DestinationNames.Of(Destination.Startup),
        [
            "Disable stops an entry starting at sign-in and removes nothing. Enable lets it start again.",
        ]),
        new(DestinationNames.Of(Destination.Memory),
        [
            "Ask to close asks a program to close itself, as its own close button would, and nothing more. The program can refuse, and Deguffer never forces it or closes a service.",
        ]),
    ];
}
