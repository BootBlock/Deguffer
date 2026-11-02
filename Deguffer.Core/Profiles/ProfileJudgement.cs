using System.Security.Principal;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Profiles;

/// <summary>
/// Whether one profile may be offered for removal. Policy only: every fact it reads was gathered
/// before it is asked (<see cref="ProfileSurvey"/>), so each rule here is testable on its own.
///
/// <para><b>Proven gone, or it stays.</b> A profile removed in error is a person's documents gone for
/// good, so a profile is offered only where every rule below holds, and the first that does not is the
/// sentence the report gives. The account must be a local account of this machine, because only this
/// machine's own account database answers for it with authority. A domain account fails to resolve on a
/// machine away from its domain controller exactly as a deleted one does, and that refusal is never read
/// as absence.</para>
///
/// <para><b>§5.2: only a recognised folder.</b> <c>DeleteProfileW</c> removes whatever folder Windows
/// records, so the folder must be directly inside the folder Windows keeps profiles in, be neither its
/// Default nor its Public profile, be named by no other entry, and be a folder rather than a link to
/// one.</para>
/// </summary>
public static class ProfileJudgement
{
    /// <summary>
    /// Microsoft Entra accounts' identifiers begin with this. They resolve only through Microsoft
    /// Entra, so this machine cannot say one is gone.
    /// </summary>
    private const string EntraPrefix = "S-1-12-1-";

    /// <param name="facts">What was found about the profile.</param>
    /// <param name="listing">Every entry in Windows' list, read at the same moment.</param>
    /// <param name="machineDomain">This machine's own account domain, or null where Windows did not say.</param>
    /// <param name="environment">The account Deguffer is running as, whose profile is never offered.</param>
    public static ProfileStanding Judge(
        ProfileFacts facts,
        ProfileListing listing,
        string? machineDomain,
        IUserEnvironment environment)
    {
        var profile = facts.Profile;

        if (Identifier(profile.Sid) is not { } sid)
        {
            return ProfileStanding.Unrecognised;
        }

        if (IsRunningAccount(profile, environment))
        {
            return ProfileStanding.RunningAccount;
        }

        // Before the load: Windows' own accounts' profiles are loaded whenever Windows runs, and are no
        // one's to report as in use.
        if (!IsAccount(sid.Value))
        {
            return ProfileStanding.WindowsAccount;
        }

        if (profile.Load is not ProfileLoad.NotLoaded)
        {
            return profile.Load is ProfileLoad.Loaded ? ProfileStanding.Loaded : ProfileStanding.LoadUnknown;
        }

        if (AccountStanding(sid, facts.Account, machineDomain) is { } account)
        {
            return account;
        }

        return FolderStanding(facts, listing) ?? ProfileStanding.Offered;
    }

    /// <summary>
    /// Whether <paramref name="sid"/> is a local account of this machine, which is the only kind of
    /// account Windows can prove is gone.
    /// </summary>
    public static bool IsLocalAccount(string sid, string? machineDomain) =>
        machineDomain is not null
        && Identifier(sid)?.AccountDomainSid is { } domain
        && string.Equals(domain.Value, machineDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Windows' answer about <paramref name="sid"/>'s account means anything to report.</summary>
    public static bool IsAccount(string sid) =>
        sid.StartsWith(EntraPrefix, StringComparison.OrdinalIgnoreCase) || Identifier(sid)?.AccountDomainSid is not null;

    private static SecurityIdentifier? Identifier(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid);
        }
        catch (ArgumentException)
        {
            // An entry Windows named otherwise, such as "S-1-5-21-...-1001.bak". Nothing is known about
            // whose it is, so nothing is decided on its behalf.
            return null;
        }
    }

    /// <summary>
    /// By identifier and by folder: an elevated Deguffer runs as the administrator who approved it, and
    /// a folder that holds or is inside its profile would take that profile with it whatever the
    /// identifier says.
    /// </summary>
    private static bool IsRunningAccount(UserProfile profile, IUserEnvironment environment)
    {
        if (string.Equals(profile.Sid, environment.UserSecurityIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var own = Path.TrimEndingDirectorySeparator(environment.UserProfile);

        return profile.Folder is { } folder
            && own.Length > 0
            && (LongPath.Contains(folder, own) || LongPath.Contains(own, folder));
    }

    private static ProfileStanding? AccountStanding(SecurityIdentifier sid, AccountReading? account, string? machineDomain)
    {
        if (sid.Value.StartsWith(EntraPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return account?.Answer is AccountAnswer.Exists ? ProfileStanding.AccountExists : ProfileStanding.CloudAccount;
        }

        if (machineDomain is null)
        {
            return ProfileStanding.MachineDomainUnknown;
        }

        if (!IsLocalAccount(sid.Value, machineDomain))
        {
            return account?.Answer is AccountAnswer.Exists ? ProfileStanding.AccountExists : ProfileStanding.DomainAccount;
        }

        return account?.Answer switch
        {
            AccountAnswer.NotFound => null,
            AccountAnswer.Exists => ProfileStanding.AccountExists,
            _ => ProfileStanding.AccountUnanswered,
        };
    }

    private static ProfileStanding? FolderStanding(ProfileFacts facts, ProfileListing listing)
    {
        if (facts.Profile.Folder is not { } folder)
        {
            return ProfileStanding.NoFolder;
        }

        if (!IsRecognisedFolder(folder, listing.Folders))
        {
            return ProfileStanding.OutsideProfiles;
        }

        if (listing.Profiles.Any(other =>
                !string.Equals(other.Sid, facts.Profile.Sid, StringComparison.OrdinalIgnoreCase)
                && other.Folder is { } theirs
                && SameFolder(theirs, folder)))
        {
            return ProfileStanding.SharedFolder;
        }

        return facts.FolderPresence switch
        {
            PathPresence.Absent => ProfileStanding.FolderAbsent,
            PathPresence.Refused => ProfileStanding.FolderRefused,
            _ when facts.FolderIsLink is not false => ProfileStanding.FolderIsLink,
            _ => null,
        };
    }

    /// <summary>
    /// Directly inside the folder Windows keeps profiles in, and neither of the two profiles there that
    /// belong to no account.
    /// </summary>
    private static bool IsRecognisedFolder(string folder, ProfileFolders folders)
    {
        if (folders.Profiles is not { } profiles || Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) is not { } parent)
        {
            return false;
        }

        return SameFolder(parent, profiles)
            && !(folders.Default is { } shipped && SameFolder(folder, shipped))
            && !(folders.Public is { } shared && SameFolder(folder, shared));
    }

    private static bool SameFolder(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(LongPath.Display(first)),
            Path.TrimEndingDirectorySeparator(LongPath.Display(second)),
            StringComparison.OrdinalIgnoreCase);
}
