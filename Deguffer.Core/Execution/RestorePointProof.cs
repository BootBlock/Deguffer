using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Scanning;
using Deguffer.Core.SystemProtection;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 for a <see cref="RemoveRestorePointsStep"/>: the restore point it kept is still listed, every
/// shadow copy that cannot be a restore point's is still listed, and System Protection's limit on each
/// volume is what it was.
///
/// <para><b>Each is asked of Windows, never of the step's outcome.</b> A removal that reported success
/// is not evidence about what it left, which is the whole of §5.6.</para>
///
/// <para><b>A missing survivor is a failure, not a removal from outside.</b> A path's check can show
/// that nothing in the run reached it. Nothing can show that here: Windows documents no link from a
/// restore point to its shadow copies, so a removal could have taken a copy System Restore shares with
/// something else, and that is exactly the over-reach this check exists to report.</para>
/// </summary>
internal static class RestorePointProof
{
    public static IEnumerable<VerificationCheck> Checks(RemoveRestorePointsStep step, ISystemProtection protection)
    {
        yield return KeptPoint(step.Kept, protection.ListRestorePoints());

        if (step.OtherCopies.Count > 0)
        {
            var copies = protection.ListShadowCopies();

            foreach (var copy in step.OtherCopies)
            {
                yield return OtherCopy(copy, copies);
            }
        }

        if (step.Storage.Count > 0)
        {
            var storage = protection.ReadStorage();

            // Asked only once a limit has changed, and then once for every volume.
            var pointInTime = new Lazy<PointInTimeRestore>(protection.ReadPointInTimeRestore);

            foreach (var volume in step.Storage.Where(v => v.Storage.Statement is Statement.Stated))
            {
                yield return Limit(
                    volume,
                    storage.FirstOrDefault(v => string.Equals(v.Volume, volume.Volume, StringComparison.OrdinalIgnoreCase)),
                    pointInTime);
            }
        }
    }

    private static VerificationCheck KeptPoint(RestorePoint kept, RestorePointListing listing)
    {
        const string reason = "The newest restore point when the clean was planned, which is always kept.";
        var subject = $"The restore point {kept.Label}";

        return listing.Answer switch
        {
            ListingAnswer.Listed when listing.Points.Contains(kept) =>
                new VerificationCheck(subject, reason, VerificationOutcome.Survived, "Still listed by System Restore."),

            ListingAnswer.Listed => new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Failed,
                "MISSING — System Restore no longer lists it."
                + (listing.Newest is { } newest ? $" The newest restore point it lists is {newest.Label}." : " It lists no restore point at all.")),

            _ => new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — System Restore did not list its restore points after the clean, so nothing shows "
                + "whether it survived."),
        };
    }

    private static VerificationCheck OtherCopy(ShadowCopy copy, ShadowCopyListing listing)
    {
        const string reason = "A shadow copy that is not one of System Restore's, which removing restore points must leave.";

        return listing.Answer switch
        {
            ListingAnswer.Listed when listing.Lists(copy.Id) =>
                new VerificationCheck(copy.Named, reason, VerificationOutcome.Survived, "Still listed by the Volume Shadow Copy service."),

            ListingAnswer.Listed => new VerificationCheck(
                copy.Named,
                reason,
                VerificationOutcome.Failed,
                "MISSING — the Volume Shadow Copy service no longer lists it. Removing a restore point may have "
                + "taken it, or the program that made it may have removed it after the scan."),

            _ => new VerificationCheck(
                copy.Named,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — the Volume Shadow Copy service did not list its shadow copies after the clean, so "
                + "nothing shows whether it survived."),
        };
    }

    /// <summary>
    /// <para><b>A change point-in-time restore could have made is not proved to be the clean's.</b> That
    /// feature sets this same limit from its own maximum usage, whenever it chooses, so where Windows has it
    /// a changed limit is reported as changed without being called a failure.</para>
    /// </summary>
    private static VerificationCheck Limit(VolumeShadowStorage before, VolumeShadowStorage? after, Lazy<PointInTimeRestore> pointInTime)
    {
        var subject = $"System Protection's limit on {before.Volume}";
        const string reason = "A setting removing restore points never changes.";

        if (after is not { Storage.Statement: Statement.Stated } stated)
        {
            return new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Unverified,
                "NOT CHECKED — Windows did not state the volume's shadow copy storage after the clean, so nothing "
                + "shows whether its limit changed.");
        }

        if (stated.Storage.MaximumBytes == before.Storage.MaximumBytes)
        {
            return new VerificationCheck(subject, reason, VerificationOutcome.Survived, $"Still {Described(before.Storage.MaximumBytes)}.");
        }

        var changed = $"CHANGED — it was {Described(before.Storage.MaximumBytes)} and is now {Described(stated.Storage.MaximumBytes)}.";

        return pointInTime.Value.Installed
            ? new VerificationCheck(
                subject,
                reason,
                VerificationOutcome.Unverified,
                $"{changed} Point-in-time restore sets this limit too, from its own maximum usage, so the change may "
                + "be its own rather than the clean's.")
            : new VerificationCheck(subject, reason, VerificationOutcome.Failed, changed);
    }

    private static string Described(long? maximum) => maximum is { } bytes ? FreeSpace.Format(bytes) : "no limit";
}
