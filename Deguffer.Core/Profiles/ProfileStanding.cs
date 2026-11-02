using Deguffer.Core.Safety;

namespace Deguffer.Core.Profiles;

/// <summary>
/// Whether a profile may be offered for removal, and where it may not, the first reason why. Only
/// <see cref="Offered"/> is offered, and every other value is a reason the profile is reported and left.
/// </summary>
public enum ProfileStanding
{
    /// <summary>A local account Windows says no longer exists, and nothing else stands in the way.</summary>
    Offered,

    /// <summary>The entry's name is not a security identifier, such as the <c>.bak</c> copy of a profile.</summary>
    Unrecognised,

    /// <summary>The profile of the account Deguffer is running as, or a folder that holds or is inside it.</summary>
    RunningAccount,

    /// <summary>Windows has the profile's hive loaded, so something is using it.</summary>
    Loaded,

    /// <summary>Windows did not say whether the profile's hive is loaded.</summary>
    LoadUnknown,

    /// <summary>An account Windows itself runs as, such as a service, which belongs to no account domain.</summary>
    WindowsAccount,

    /// <summary>A Microsoft Entra account, which only Microsoft Entra can say is gone.</summary>
    CloudAccount,

    /// <summary>Windows did not say which account domain is this machine's, so no account can be proved local.</summary>
    MachineDomainUnknown,

    /// <summary>
    /// An account of a domain other than this machine's. Away from its domain controller, a live domain
    /// account fails to resolve too, so its failure to resolve proves nothing.
    /// </summary>
    DomainAccount,

    /// <summary>Windows named the account, so it still exists.</summary>
    AccountExists,

    /// <summary>Windows did not answer whether the account exists, which is never an answer that it does not.</summary>
    AccountUnanswered,

    /// <summary>The entry names no folder Deguffer could read.</summary>
    NoFolder,

    /// <summary>
    /// The folder is not directly inside the folder Windows keeps profiles in, or is its Default or Public
    /// profile, or Windows did not say where it keeps profiles.
    /// </summary>
    OutsideProfiles,

    /// <summary>Another entry in Windows' list names the same folder.</summary>
    SharedFolder,

    /// <summary>The folder is not there: the entry is all that is left.</summary>
    FolderAbsent,

    /// <summary>Windows would not say whether the folder is there.</summary>
    FolderRefused,

    /// <summary>The folder is a link to somewhere else.</summary>
    FolderIsLink,
}

/// <summary>What was found about one profile, before anything is decided about it.</summary>
/// <param name="Account">What Windows answered about its account, or null where it was not asked.</param>
/// <param name="FolderPresence">Whether its folder is there, or <see cref="PathPresence.Absent"/> where it names none.</param>
/// <param name="FolderIsLink">Whether its folder is a link, where Windows said.</param>
public sealed record ProfileFacts(UserProfile Profile, AccountReading? Account, PathPresence FolderPresence, bool? FolderIsLink);

/// <summary>One profile, what was found about it, and what was decided.</summary>
public sealed record ProfileVerdict(ProfileFacts Facts, ProfileStanding Standing)
{
    public UserProfile Profile => Facts.Profile;
}
