using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 for a plan's <see cref="RemoveProfileStep"/>s: every profile the plan did not set out to remove,
/// the running account's among them, is still in Windows' list with the same folder, and that folder is
/// still there, as is the folder Windows keeps profiles in.
///
/// <para><b>Asked of the plan, not of each step.</b> Every step carries the same list, and a profile one
/// step removes is not a survivor another step's check should miss. So every profile any of the plan's
/// steps removes is left out, and each other profile is checked once.</para>
///
/// <para><b>Each is asked of Windows, never of the outcome.</b> A removal that reported success is not
/// evidence about what it left. A missing profile is a failure rather than a removal from outside, for the
/// reason <see cref="RestorePointProof"/> gives: nothing can show that Windows' removal did not reach
/// it.</para>
/// </summary>
internal static class ProfileRemovalProof
{
    private const string OtherReason = "Another profile, which removing a profile must leave exactly as it was.";

    private const string RunningReason = "The profile of the account Deguffer is running as, which is never removed.";

    public static IEnumerable<VerificationCheck> Checks(IReadOnlyList<RemoveProfileStep> removals, IUserProfiles profiles)
    {
        if (removals.Count == 0)
        {
            yield break;
        }

        var removed = removals.Select(step => step.Profile.Sid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = profiles.List();

        if (removals[0].ProfilesFolder is { } folder)
        {
            yield return ProfilesFolder(folder);
        }

        foreach (var before in removals[0].Listed.Where(verdict => !removed.Contains(verdict.Profile.Sid)))
        {
            yield return Survivor(before, after);
        }
    }

    private static VerificationCheck ProfilesFolder(string folder)
    {
        const string reason = "The folder Windows keeps every profile in.";

        return LongPath.ProbeDirectory(folder) switch
        {
            PathPresence.Present => new VerificationCheck(folder, reason, VerificationOutcome.Survived, "Still present."),
            PathPresence.Absent => new VerificationCheck(folder, reason, VerificationOutcome.Failed, "MISSING — it is no longer there."),
            _ => new VerificationCheck(
                folder, reason, VerificationOutcome.Unverified, "NOT CHECKED — Windows would not say whether it is there."),
        };
    }

    private static VerificationCheck Survivor(ProfileVerdict before, ProfileListing after)
    {
        var subject = ProfileReport.Name(before.Profile);
        var reason = before.Standing is ProfileStanding.RunningAccount ? RunningReason : OtherReason;

        if (after.Presence is not PathPresence.Present)
        {
            return new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — Windows did not list its profiles after the clean, so nothing shows whether it survived.");
        }

        if (after.Find(before.Profile.Sid) is not { } now)
        {
            return new VerificationCheck(
                subject, reason, VerificationOutcome.Failed, "MISSING — Windows no longer lists this profile.");
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(now.Folder ?? string.Empty),
                Path.TrimEndingDirectorySeparator(before.Profile.Folder ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            return new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Failed,
                now.Folder is { } moved
                    ? $"CHANGED — Windows now records its folder as {LongPath.Display(moved)}."
                    : "CHANGED — Windows no longer records a folder for it.");
        }

        if (before.Facts.FolderPresence is not PathPresence.Present || before.Profile.Folder is not { } folder)
        {
            return new VerificationCheck(subject, reason, VerificationOutcome.Survived, "Still listed by Windows, with the same folder.");
        }

        return LongPath.ProbeDirectory(folder) switch
        {
            PathPresence.Present => new VerificationCheck(
                subject, reason, VerificationOutcome.Survived, "Still listed by Windows, and its folder is still there."),
            PathPresence.Absent => new VerificationCheck(
                subject, reason, VerificationOutcome.Failed, "MISSING — Windows still lists it, but its folder is gone."),
            _ => new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — Windows still lists it, but would not say whether its folder is there."),
        };
    }
}
