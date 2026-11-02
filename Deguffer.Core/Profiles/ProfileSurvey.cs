using Deguffer.Core.Safety;

namespace Deguffer.Core.Profiles;

/// <summary>
/// Every profile Windows lists, with what was found about each and what
/// <see cref="ProfileJudgement"/> decided, read at one moment.
///
/// <para>Taken by the plan and again by the run immediately before a removal, so the two decide on the
/// same rules from the machine as it then stands: an account can be made again, and a profile loaded,
/// while the preview is on screen.</para>
/// </summary>
public sealed record ProfileSurvey(ProfileListing Listing, IReadOnlyList<ProfileVerdict> Verdicts)
{
    /// <summary>
    /// Ask Windows for its list and, for each entry, whether its account exists and whether its folder is
    /// there. Blocks: every question is a call into Windows, and an account lookup can wait on a domain.
    /// </summary>
    public static ProfileSurvey Take(IUserProfiles profiles, IUserEnvironment environment)
    {
        var listing = profiles.List();

        if (listing.Presence is not PathPresence.Present)
        {
            return new ProfileSurvey(listing, []);
        }

        var machineDomain = profiles.MachineAccountDomain();
        var verdicts = listing.Profiles
            .Select(profile => Facts(profiles, profile))
            .Select(facts => new ProfileVerdict(facts, ProfileJudgement.Judge(facts, listing, machineDomain, environment)))
            .ToList();

        return new ProfileSurvey(listing, verdicts);
    }

    /// <summary>The verdict on the entry named <paramref name="sid"/>, or null where Windows no longer lists it.</summary>
    public ProfileVerdict? Find(string sid) =>
        Verdicts.FirstOrDefault(verdict => string.Equals(verdict.Profile.Sid, sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Only an account's identifier is looked up: a service's or a malformed entry's answer could say
    /// nothing the report needs, and a lookup can wait on a domain controller.
    /// </summary>
    private static ProfileFacts Facts(IUserProfiles profiles, UserProfile profile)
    {
        var account = ProfileJudgement.IsAccount(profile.Sid) ? profiles.LookUp(profile.Sid) : null;

        if (profile.Folder is not { } folder)
        {
            return new ProfileFacts(profile, account, PathPresence.Absent, FolderIsLink: null);
        }

        var presence = LongPath.ProbeDirectory(folder, out var isLink);

        return new ProfileFacts(profile, account, presence, isLink);
    }
}
