using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// §5.6 for a <see cref="ReleaseLocalCopiesStep"/>: every file it named must still be there, and still
/// be a file the sync app can hand back.
///
/// <para><b>The opposite of a deletion's negative, and the same alarm.</b> Releasing a local copy
/// destroys nothing, so a file this step named that has gone is exactly what an over-reaching rule
/// looks like here. A missing one is judged as any protected path is, so a file whose whole folder
/// was removed by something outside the run reads as that rather than as Deguffer's doing.</para>
///
/// <para><b>Present and no longer a placeholder is still a survivor.</b> The sync app turned it back
/// into an ordinary file, which keeps every byte on this PC. That is its choice to make, and nothing
/// is lost by it.</para>
///
/// <para><b>Only the readings go to other threads.</b> Each is an open and a describe that waits on
/// the disk, and a large account names tens of thousands, so they are read up to
/// <see cref="LocalCopyRelease.ReleasesAtOnce"/> at a time. The checks are made afterwards on the
/// calling thread, in the step's order: a run reports the same list however the reads
/// interleaved.</para>
/// </summary>
internal static class ReleasedFileProof
{
    /// <param name="missing">How the verification judges a file it no longer finds, as a protected path.</param>
    public static IEnumerable<VerificationCheck> Checks(
        ReleaseLocalCopiesStep release,
        ICloudFiles cloud,
        Func<ProtectedPath, VerificationCheck> missing,
        CancellationToken ct)
    {
        var readings = new PlaceholderReading[release.Files.Count];

        // The helpers are threads of the proof's own, never the pool's, because each open blocks its
        // thread on the disk. The calling thread is the verification's, which its callers start on
        // WorkerThreads for the same reason. See ListWorkers.
        ListWorkers.Run(
            readings.Length,
            LocalCopyRelease.ReleasesAtOnce,
            index => readings[index] = cloud.Read(release.Files[index].Path),
            ct);

        var reason = $"A file {release.SyncApp} was asked to keep only in the cloud. The file itself must stay.";

        for (var i = 0; i < readings.Length; i++)
        {
            yield return Check(release.Files[i].Path, readings[i], release.SyncApp, reason, missing);
        }
    }

    private static VerificationCheck Check(
        string path,
        PlaceholderReading reading,
        string syncApp,
        string reason,
        Func<ProtectedPath, VerificationCheck> missing) => reading switch
    {
        { Presence: PathPresence.Present, Placeholder: not null } =>
            new VerificationCheck(path, reason, VerificationOutcome.Survived, "Still present, and still a cloud file."),

        { Presence: PathPresence.Present } =>
            new VerificationCheck(
                path, reason, VerificationOutcome.Survived,
                $"Still present. {syncApp} has turned it back into an ordinary file, so all of it is on this PC."),

        // Measured while the plan was made, so it was there then: the claim NarrowedTo makes about a
        // declined step, for the reason it gives.
        _ => missing(new ProtectedPath(path, reason, PathPresence.Present)),
    };
}
