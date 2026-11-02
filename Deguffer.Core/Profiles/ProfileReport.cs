using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Profiles;

/// <summary>
/// The sentences that report a profile: what is known about it, and why it is not offered where it is
/// not. Shared by the preview and by a run that finds a profile changed since, so the two cannot come to
/// give different reasons for the same rule.
///
/// <para>A profile is named by its folder, which is often a person's name. These sentences are shown on
/// screen and never written to a log, and a report that quotes one redacts it
/// (<see cref="Diagnostics.ReportRedaction"/>).</para>
/// </summary>
public static class ProfileReport
{
    /// <summary>
    /// One profile, as the preview reports it: its size, when it was last used, whether its account
    /// exists, whether it is in use, and why it is not offered.
    /// </summary>
    /// <param name="measured">
    /// What its folder measured and whether the measurement reached it, or null where it was not
    /// measured.
    /// </param>
    public static string Line(ProfileVerdict verdict, (ScanSize Size, bool Reached)? measured)
    {
        var facts = new List<string>(4);

        if (measured is { } folder)
        {
            facts.Add(folder.Reached ? FreeSpace.Format(folder.Size.Reclaimable) : "its size could not be measured");
        }

        facts.Add(verdict.Profile.LastUsed is { } used
            ? $"last used {used.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : "Windows did not record when it was last used");
        facts.Add(Account(verdict.Facts.Account));
        facts.Add(verdict.Profile.Load switch
        {
            ProfileLoad.Loaded => "in use",
            ProfileLoad.NotLoaded => "not in use",
            _ => "Windows did not say whether it is in use",
        });

        var line = $"{Name(verdict.Profile)}: {string.Join(", ", facts)}.";

        return verdict.Standing is ProfileStanding.Offered ? line : $"{line} Not offered, because {WhyNot(verdict.Standing)}";
    }

    /// <summary>The profile by the folder Windows records for it, or by its entry where it records none.</summary>
    public static string Name(UserProfile profile) =>
        profile.Folder is { } folder ? $"The profile at {LongPath.Display(folder)}" : $"The profile entry {profile.Sid}";

    /// <summary>Why a profile of this standing is not offered, as a clause that completes "because".</summary>
    public static string WhyNot(ProfileStanding standing) => standing switch
    {
        ProfileStanding.Unrecognised =>
            "Windows' entry for it is not named after an account, as the copy Windows keeps of a profile it could not load is.",
        ProfileStanding.RunningAccount => "it is the profile of the account Deguffer is running as.",
        ProfileStanding.Loaded => "Windows has it loaded, so something is using it.",
        ProfileStanding.LoadUnknown => "Windows did not say whether something is using it.",
        ProfileStanding.WindowsAccount => "it belongs to an account Windows itself runs as, not to a person.",
        ProfileStanding.CloudAccount =>
            "it belongs to a Microsoft Entra account, and only Microsoft Entra can say whether such an account is gone.",
        ProfileStanding.MachineDomainUnknown =>
            "Windows did not say which accounts are this PC's own, so no account could be proved gone.",
        ProfileStanding.DomainAccount =>
            "it belongs to a domain account. A domain account Windows cannot reach looks the same as one that was "
            + "deleted, so this PC cannot prove it is gone.",
        ProfileStanding.AccountExists =>
            "its account still exists. Removing the account in Settings, Accounts, Other users offers to remove "
            + "its profile too.",
        ProfileStanding.AccountUnanswered => "Windows did not say whether its account still exists.",
        ProfileStanding.NoFolder => "Windows records no folder for it.",
        ProfileStanding.OutsideProfiles => "its folder is not one Windows keeps profiles in.",
        ProfileStanding.SharedFolder =>
            "another entry in Windows' list of profiles names the same folder, one inside it, or one that holds it.",
        ProfileStanding.BackupEntry =>
            "Windows keeps a second entry for the same account, a copy of a profile it could not load, which "
            + "Windows' removal may act on too.",
        ProfileStanding.FolderAbsent => "its folder is already gone, and only Windows' entry for it is left.",
        ProfileStanding.FolderRefused => "Windows would not say whether its folder is there.",
        ProfileStanding.FolderIsLink => "its folder is a link to somewhere else, which a removal could follow.",
        _ => throw new ArgumentOutOfRangeException(nameof(standing), standing, "An offered profile has no reason not to be."),
    };

    private static string Account(AccountReading? account) => account switch
    {
        null => "it belongs to no person's account",
        { Answer: AccountAnswer.Exists } => "its account exists",
        { Answer: AccountAnswer.NotFound } => "Windows found no account for it",
        { Error: var error } => $"Windows did not say whether its account exists (error {error})",
    };
}
