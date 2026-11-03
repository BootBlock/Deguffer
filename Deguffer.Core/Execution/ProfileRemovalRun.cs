using Deguffer.Core.Profiles;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="RemoveProfileStep"/>: the profile is judged
/// again, looked into for an Outlook data file, removed by Windows, and then looked for in Windows' list
/// and on the disk.
///
/// <para>Apart from the executor's own removals because Windows removes the folder, not Deguffer, for
/// the reason <see cref="RestorePointRun"/> is.</para>
/// </summary>
internal static class ProfileRemovalRun
{
    public static async Task<StepOutcome> RunAsync(
        IUserProfiles profiles,
        IUserEnvironment environment,
        IDirectoryScanner scanner,
        RemoveProfileStep step,
        StepClock clock,
        CancellationToken ct)
    {
        var survey = await Task.Run(() => ProfileSurvey.Take(profiles, environment), ct).ConfigureAwait(false);

        if (WhyNotNow(step, survey) is { } changed)
        {
            return NotRun(step, changed);
        }

        // §9, looked for again immediately before the removal, because Windows removes a profile whole
        // and a data file can arrive between the preview and the clean. A folder Windows will not list
        // could hold one.
        var look = await clock.LookAsync([step.Folder], MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Not removed", "Windows removes a profile whole and cannot be told to leave anything", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        // Windows' removal cannot be told to stop, so a cancelled clean lets it finish.
        var removal = await Task.Run(() => profiles.Remove(step.Profile.Sid), CancellationToken.None).ConfigureAwait(false);

        var listed = await Task.Run(profiles.List, CancellationToken.None).ConfigureAwait(false);
        var folder = LongPath.ProbeDirectory(step.Folder);

        return await OutcomeAsync(step, removal, listed, folder, scanner).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the profile must not be removed now, or null where it may. Judged on the rules the plan was
    /// judged on, against the machine as it stands now, and the folder must be the one the preview showed.
    /// </summary>
    private static string? WhyNotNow(RemoveProfileStep step, ProfileSurvey survey)
    {
        if (survey.Listing.Presence is not PathPresence.Present)
        {
            return "Windows did not list its profiles, so nothing showed that this one could still be removed.";
        }

        if (survey.Find(step.Profile.Sid) is not { } now)
        {
            return "Windows no longer lists this profile.";
        }

        if (now.Standing is not ProfileStanding.Offered)
        {
            return $"it is no longer offered, because {ProfileReport.WhyNot(now.Standing)}";
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(now.Profile.Folder ?? string.Empty),
            Path.TrimEndingDirectorySeparator(step.Folder),
            StringComparison.OrdinalIgnoreCase)
                ? null
                : "Windows now records a different folder for this profile than the one the preview showed.";
    }

    private static async Task<StepOutcome> OutcomeAsync(
        RemoveProfileStep step,
        ProfileRemoval removal,
        ProfileListing listed,
        PathPresence folder,
        IDirectoryScanner scanner)
    {
        var entryGone = listed.Presence is PathPresence.Present && listed.Find(step.Profile.Sid) is null;
        var folderGone = folder is PathPresence.Absent;

        if (removal.Answer is ProfileRemovalAnswer.Removed && entryGone && folderGone)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: step.EstimatedBytes,
                Refusals.None,
                "Windows removed the profile: its folder and its entry in Windows' list of profiles are both gone.");
        }

        var what = removal.Answer switch
        {
            ProfileRemovalAnswer.Removed => "Windows reported the profile removed, but",
            ProfileRemovalAnswer.NeedsElevation => "Windows removes a profile only for an administrator, and",
            _ => $"Windows did not remove the profile (error {removal.Error}), and",
        };

        var left = (entryGone, folder) switch
        {
            (true, PathPresence.Absent) => " both its folder and its entry are gone.",
            (false, PathPresence.Absent) => " its entry is still in Windows' list, though its folder is gone.",
            (true, _) => " its folder is still there, though its entry is gone from Windows' list.",
            _ => " its folder and its entry are both still there.",
        };

        // What was taken is what the plan measured less what is still there. Measured again only because
        // the folder changed: a removal that stopped part-way took some of it.
        var reclaimed = folderGone ? step.EstimatedBytes : await TakenAsync(step, scanner).ConfigureAwait(false);
        var counted = reclaimed is null
            ? " What is left could not be measured, so nothing is counted as freed."
            : string.Empty;

        return new StepOutcome(step.Description, Succeeded: false, BytesReclaimed: reclaimed ?? 0, Refusals.None, what + left + counted);
    }

    private static async Task<long?> TakenAsync(RemoveProfileStep step, IDirectoryScanner scanner)
    {
        var after = await scanner.MeasureFromDiskAsync([new ScanRequest(step.Folder)], CancellationToken.None).ConfigureAwait(false);

        return after[0].WasReached ? Math.Max(0, step.EstimatedBytes - after[0].Size.Reclaimable) : null;
    }

    private static StepOutcome NotRun(RemoveProfileStep step, string why) =>
        new(step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, $"Nothing was removed: {why}");
}
