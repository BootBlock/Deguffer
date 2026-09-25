using System.Diagnostics;
using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Carries out a plan. Holds no knowledge of any cache — it dispatches the steps a provider
/// already decided on, and reports what happened.
/// </summary>
/// <param name="refusals">
/// Where each removal records the places Windows refused, so the next preview of the same location
/// can leave out what is still refused. Required rather than defaulted: the default would be the
/// signed-in user's own record, and a caller that forgot to pass one would write into it.
/// </param>
/// <param name="cloud">
/// How a <see cref="ReleaseLocalCopiesStep"/> is carried out and its files proved standing, for the one
/// provider that plans one. Defaulted for the reason <paramref name="emptier"/> is.
/// </param>
public sealed class PlanExecutor(
    IProcessRunner runner,
    IDirectoryScanner scanner,
    RefusalRecord refusals,
    IRecycleBinEmptier? emptier = null,
    ICloudFiles? cloud = null)
{
    private readonly IRecycleBinEmptier _emptier = emptier ?? ShellRecycleBinEmptier.Default;
    private readonly ICloudFiles _cloud = cloud ?? CloudFiles.Default;

    /// <param name="runReach">
    /// What the whole run may destroy. §5.6's negative is answered against it rather than against
    /// this plan alone, because a run is many plans and a folder another provider deleted is not a
    /// folder something outside Deguffer deleted. Null means this plan is the whole run.
    /// </param>
    /// <param name="residue">
    /// What the run's removals have left standing, which each removal here adds to and §5.6 then
    /// reads — see <see cref="RunResidue"/>. Null means this plan is the whole run, and it is given a
    /// record of its own.
    /// </param>
    public async Task<CleanupResult> ExecuteAsync(
        CleanupPlan plan,
        RunReach? runReach,
        RunResidue? residue,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var leftStanding = residue ?? new RunResidue();
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<StepOutcome>(plan.Steps.Count);

        // The same weighting the planner applies to whole plans, for the same reason: one obj
        // directory of 4 GB and five of 20 MB are six steps, and splitting the bar six ways would
        // crawl through the first sixth and then jump the rest.
        var weights = ProgressWeights.For(plan.Steps.Select(s => s.EstimatedBytes));
        var total = weights.Sum();
        var done = 0.0;

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = plan.Steps[i];

            // Each step's own 0-to-1 becomes its slice of this plan's 0-to-1.
            var stepProgress = ScaledProgress.Within(progress, done / total, weights[i] / total);

            outcomes.Add(step switch
            {
                RunCommandStep command => await RunCommandAsync(command, ct).ConfigureAwait(false),
                ClearDirectoryStep clear => await ClearAsync(clear, plan.Keep, leftStanding, stepProgress, ct).ConfigureAwait(false),
                DeleteDirectoryStep delete => await DeleteAsync(delete, plan.Keep, leftStanding, stepProgress, ct).ConfigureAwait(false),
                DeleteFileStep delete => await DeleteAsync(delete, plan.Keep, stepProgress, ct).ConfigureAwait(false),
                EmptyRecycleBinStep empty => await EmptyAsync(empty, plan.Keep, stepProgress, ct).ConfigureAwait(false),
                ReleaseLocalCopiesStep release => await LocalCopyRelease.RunAsync(_cloud, release, plan.Keep, stepProgress, ct).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Unknown step type {step.GetType().Name}."),
            });

            // Reported from here rather than trusted from the step: a command step reports nothing
            // at all while it runs, and a removal that ends early would leave a gap that never
            // closes.
            done += weights[i];
            progress?.Report(done / total);
        }

        stopwatch.Stop();

        return new CleanupResult
        {
            ProviderId = plan.ProviderId,
            ProviderName = plan.ProviderName,
            Steps = outcomes,
            Duration = stopwatch.Elapsed,

            // §5.6 is not a separate user action: acting and proving what survived are one step.
            Verification = PlanVerifier.Verify(plan, runReach, leftStanding, ct, _cloud),
        };
    }

    private async Task<StepOutcome> RunCommandAsync(RunCommandStep step, CancellationToken ct)
    {
        // The "before" size was measured when the plan was built; re-walking a multi-gigabyte
        // tree to learn it again would double the cost of the operation.
        //
        // This is sound only because a plan-time figure is always freshly measured. Remembered
        // sizes exist (ScanEstimateCache) but are never returned from a measurement — they only
        // reach the screen while the real scan runs. Were a stale one to arrive here it would not
        // merely look wrong: it would inflate the reclaimed total reported below.
        //
        // MeasuredBefore wins where it is present, because the delta must subtract like from
        // like: a step whose estimate is the tool's own figure re-measures paths that never held
        // that number. See its declaration.
        //
        // The two sides are commensurable because ScanSize.Reclaimable is Logical, which both of
        // §5.5's routes measure and measure identically. This subtraction was the first place that
        // mattered — the before-figure can come from the file table and the after-figure never does
        // — and it is no longer a special case: see ScanSize.Reclaimable for why the whole tool
        // reports that axis.
        var before = (step.MeasuredBefore ?? step.Estimated).Reclaimable;

        // §9, looked for again on the disk immediately before the tool runs, because the tool cannot be
        // told to leave one file and a store can arrive between the preview and the clean. See
        // MailStoreSearch for what the look costs and why it is paid here.
        if (await StoresInsideAsync(step.MeasuredPaths, ct).ConfigureAwait(false) is { Count: > 0 } stores)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                $"Not run: an Outlook data file is inside what this command clears, at {MailStorePlan.Name(stores)}. "
                + "The tool cannot be told to leave it, and Deguffer never removes one.",
                MailStores: stores.Count);
        }

        var outcome = await runner.RunAsync(step.FileName, step.Arguments, ct).ConfigureAwait(false);

        // From the disk, never from the volume snapshot. Nothing invalidates that snapshot between
        // planning and executing — Invalidate runs once, at the top of a planning pass — so an
        // ordinary measurement here would hand back the very figure it is about to be subtracted
        // from, and a clean that freed gigabytes would report nothing.
        var after = await MeasureAllAsync(step.MeasuredPaths, ct).ConfigureAwait(false);
        var reclaimed = before - after;

        // A negative delta means the tree grew between preview and clean — a build restoring
        // packages in the background, most likely. Report what is actually still there rather
        // than clamping to zero and claiming nothing was reclaimed.
        var message = reclaimed < 0
            ? $"{outcome.Message} (the cache grew since the scan; " +
              $"{FreeSpace.Format(after)} remains)"
            : outcome.Message;

        return new StepOutcome(
            step.Description,
            outcome.Succeeded,
            BytesReclaimed: Math.Max(0, reclaimed),
            Refusals.None,
            message);
    }

    /// <summary>
    /// Hand one volume's bin to Windows, then find out what that achieved by looking at the disk.
    ///
    /// <para><b>The reclaim is measured rather than assumed.</b> <c>SHEmptyRecycleBin</c> reports
    /// one HRESULT and no figures at all, so an estimate reported as a result would be a number
    /// nobody checked — and the estimate is a plan-time measurement of a directory anything on the
    /// machine may have written to since. Subtracting a fresh reading of the same path is the same
    /// arithmetic <see cref="RunCommandAsync"/> does after a §5.1 command, for the same reason, and
    /// it costs almost nothing here: the directory it re-measures is the one just emptied.</para>
    ///
    /// <para>The measurement comes from the disk rather than the volume snapshot, which is what
    /// makes it a second reading instead of the first one handed back. See
    /// <see cref="RunCommandAsync"/>, where that was found.</para>
    ///
    /// <para>Windows leaves the account's directory standing and empty rather than removing it,
    /// which was observed rather than assumed, so nothing here reads its absence as success.</para>
    /// </summary>
    private async Task<StepOutcome> EmptyAsync(
        EmptyRecycleBinStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // The guard and this route are mutually exclusive, and RecycleBinProvider is what makes
        // them so. Re-asked here because the cost of that one expression being edited wrongly is
        // the files the user explicitly asked to keep: Windows empties a bin whole and has no way
        // to hold anything back, so this step under a guard would destroy them and §5.6 would not
        // notice, since they sit inside the target rather than beside it.
        if (keep.IsOn)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                "Nothing was removed: Windows cannot empty a Recycle Bin partially, and this plan "
                + "asked for recently changed files to be left alone.");
        }

        // A path that is not shaped like a bin, which nothing can currently build. Refused rather
        // than handed on, because the value that reaches the shell decides what the shell destroys.
        if (string.IsNullOrEmpty(step.VolumeRoot))
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                "Nothing was removed: this is not the path of a Recycle Bin on a drive.");
        }

        // §9, for the reason the guard is refused above: Windows empties the bin whole, and a store
        // deleted into it since the preview would go with everything else. Looked for on the disk,
        // because the plan was made before it arrived.
        if (await StoresInsideAsync([step.Path], ct).ConfigureAwait(false) is { Count: > 0 } stores)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                $"Nothing was removed: this Recycle Bin holds an Outlook data file, at {MailStorePlan.Name(stores)}. "
                + "Windows empties a bin whole, and Deguffer never removes one.",
                MailStores: stores.Count);
        }

        // The last honest moment to stop: the call itself cannot be cancelled once it starts, and
        // on a large bin it runs for a long time. See ShellRecycleBinEmptier.
        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => _emptier.Empty(step.VolumeRoot), ct).ConfigureAwait(false);

        var after = await scanner.MeasureFromDiskAsync(step.Path, ct).ConfigureAwait(false);
        var remaining = after.Size.Reclaimable;
        var reclaimed = step.EstimatedBytes - remaining;

        // The entries by the same subtraction. The bin's own folder stays standing and was never in
        // the estimate's count, so it comes out of the reading afterwards as well.
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - Math.Max(0, after.Size.Entries - 1));

        // Nothing to report along the way — the shell offers no progress of its own, and its
        // progress window is one of the three things the flags suppress.
        progress?.Report(1.0);

        // The disk is the evidence and the HRESULT is only the explanation, which is the whole
        // reason the measurement above is taken: SHEmptyRecycleBin reports S_OK and no figures, so
        // a success taken from the return value alone would be Deguffer repeating a claim it had
        // just measured to be false. A bin that still holds something, having given nothing up, is
        // a failed step whatever the shell said about it.
        var succeeded = remaining == 0 || reclaimed > 0;

        var message = (succeeded, remaining, outcome.Message) switch
        {
            (false, _, { } why) => $"{why} Everything in it is still there.",

            // The shell reported success and the bin is exactly as full as it was. Nothing else
            // here can say what went wrong, so the sentence says what is true.
            (false, _, null) =>
                $"Nothing was removed: Windows reported success and the bin still holds "
                + $"{FreeSpace.Format(remaining)}.",

            // Emptied, and it held nothing by the time the shell reached it. Distinguished from the
            // line above by the measurement rather than by the reclaim, which is zero in both.
            (true, _, _) when reclaimed <= 0 => "Emptied; it held nothing by then.",

            // Something went and something stayed: a file another process holds open is the usual
            // cause, and it is §5.3's ordinary outcome rather than a failure.
            (true, > 0, _) => $"Emptied, apart from {FreeSpace.Format(remaining)} Windows "
                + "would not release.",

            _ => "Emptied.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            Math.Max(0, reclaimed),
            Refusals.None,
            message,
            EntriesRemoved: entriesRemoved);
    }

    private async Task<StepOutcome> DeleteAsync(
        DeleteDirectoryStep step,
        MinimumAge keep,
        RunResidue leftStanding,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // §9, for a directory that goes whole or not at all: the walk below would step over a store that
        // arrived since the preview and take what belongs with it. Looked for on the disk, as it is before
        // Windows empties a bin. See DeleteDirectoryStep.IsIndivisible.
        if (step.IsIndivisible && await StoresInsideAsync([step.Path], ct).ConfigureAwait(false) is { Count: > 0 } arrived)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                $"Nothing was removed: this holds an Outlook data file, at {MailStorePlan.Name(arrived)}. "
                + "What is inside it goes whole or not at all, and Deguffer never removes one.",
                MailStores: arrived.Count);
        }

        var removal = await DirectoryRemover.RemoveAsync(step.Path, keep, progress, ct).ConfigureAwait(false);

        refusals.Replace(step.Path, removal.RefusedAt);
        leftStanding.Record(step.Path, removal.LeftStanding);

        // A refusal is not a failure (§5.3). The step only fails if the directory survived intact
        // and nothing at all was reclaimed — that is, we achieved nothing.
        //
        // A file the guard held back counts as something achieved, because it is the outcome the
        // user asked for. A directory holding nothing else reclaims no bytes and keeps its root, so
        // without this the setting working exactly as intended would be reported as a failed step.
        //
        // An empty folder taken out of a folder the guard kept standing is something achieved too, and
        // it reclaims no bytes.
        // An Outlook mail store left where it was is something achieved for the same reason: it is the
        // outcome §9 requires, and a folder holding nothing else would otherwise read as a failure.
        var stores = removal.MailStores.Count;

        var succeeded = removal.RootRemoved || removal.BytesReclaimed > 0 || removal.Kept > 0
            || removal.EntriesRemoved > 0 || stores > 0;

        var message = succeeded switch
        {
            false => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders),

            // Nothing came out and the folder is still standing, which is the guard's own case: it
            // held back everything this step named. Saying "Removed" here would be a false statement
            // about the user's disk, and the qualifier would not rescue it — the sentence has to be
            // about what stayed, because that is all that happened.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } && stores == 0 =>
                $"Left alone: {removal.Kept} file(s) changed too recently"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, kept: 0)}.",

            // The same shape with a store among what stayed, which the guard's sentence would misname.
            _ when removal is { BytesReclaimed: 0, RootRemoved: false, EntriesRemoved: 0 } =>
                $"Nothing was removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores)}.",

            _ => $"Removed{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, mailStores: stores)}.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            removal.Kept,
            EntriesRemoved: removal.EntriesRemoved,
            RefusedFolders: removal.RefusedFolders,
            MailStores: stores);
    }

    /// <summary>
    /// Empty a directory and leave it standing, sparing the entries the plan named.
    ///
    /// <para>The same removal as <see cref="DeleteAsync(DeleteDirectoryStep, MinimumAge, RunResidue, IProgress{double}?, CancellationToken)"/>
    /// under different bounds, rather than a second walk of its own: §6.3's extended-length paths,
    /// §5.3's skip on a refusal, the guard on recently changed files and the refusal to follow a
    /// link are all properties of that one removal, and a parallel implementation is where one of
    /// them would go missing.</para>
    ///
    /// <para><b>Success is measured differently, because there is no root to have gone.</b> A
    /// deletion can point at the directory itself; this one cannot, so what it achieved is bytes,
    /// or the guard doing its job. A scratch folder holding nothing but live and recent files
    /// reclaims nothing and has failed at nothing.</para>
    /// </summary>
    private async Task<StepOutcome> ClearAsync(
        ClearDirectoryStep step,
        MinimumAge keep,
        RunResidue leftStanding,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await DirectoryRemover.RemoveAsync(
            step.Path,
            keep,
            progress,
            ct,
            fileSystem: null,
            new RemovalBounds(KeepRoot: true, step.Spared)).ConfigureAwait(false);

        refusals.Replace(step.Path, removal.RefusedAt);
        leftStanding.Record(step.Path, removal.LeftStanding);

        // A folder Windows refused is a refusal as much as a file is. Without it, a clear whose only
        // outcome was a folder a program is working in would pass as a folder that held nothing.
        var stores = removal.MailStores.Count;

        var succeeded = removal.BytesReclaimed > 0 || removal.EntriesRemoved > 0 || removal.Kept > 0
            || removal.Spared > 0 || stores > 0 || (removal.Refused.IsEmpty && removal.RefusedFolders.IsEmpty);

        // Asked of the entries as well as the bytes: a folder holding only empty folders reclaims no
        // bytes, and it was still cleared.
        var message = (succeeded, removal.BytesReclaimed, removal.EntriesRemoved) switch
        {
            (false, _, _) => LeftInPlace.WhyNothingHappened(removal.Refused, removal.RefusedFolders),

            // Nothing came out, and the reasons are the ones the step promised: files too recent to
            // touch, entries something is using, and Outlook mail stores. "Cleared" would be a false
            // statement about a folder that is exactly as full as it was.
            (true, 0, 0) when removal.Kept > 0 || removal.Spared > 0 || stores > 0 =>
                "Nothing was cleared"
                + $"{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores)}.",

            (true, 0, 0) => "It held nothing to clear.",

            _ => $"Cleared{LeftInPlace.Clauses(removal.Refused, removal.RefusedFolders, removal.Kept, removal.Spared, stores)}.",
        };

        return new StepOutcome(
            step.Description,
            succeeded,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            removal.Kept,
            removal.Spared,
            removal.EntriesRemoved,
            removal.RefusedFolders,
            stores);
    }

    private async Task<StepOutcome> DeleteAsync(
        DeleteFileStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var removal = await FileRemover.RemoveAsync(step.Path, keep, ct).ConfigureAwait(false);

        // One file, so there is no fraction to report along the way — only the end of it.
        progress?.Report(1.0);

        // Kept is a success for the same reason it is on a directory: nothing was removed because
        // nothing was meant to be. There is only the one file, so the whole message says so rather
        // than qualifying a removal that did not happen.
        //
        // The record is left as it was, because nothing asked Windows anything: a refusal recorded
        // by an earlier clean is still the latest answer there is.
        if (removal.Kept)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: 0,
                Refusals.None,
                "Left alone: it changed too recently.",
                Kept: 1);
        }

        // The same success for the same reason: a store is never removed, so leaving it is the step
        // doing what it must. Nothing asked Windows anything here either, so the record stays as it
        // was.
        if (removal.MailStore)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: true,
                BytesReclaimed: 0,
                Refusals.None,
                "Left alone: it is an Outlook data file, and Deguffer never removes one.",
                MailStores: 1);
        }

        refusals.Replace(step.Path, removal.Refused.IsEmpty ? [] : [step.Path]);

        var message = removal.Removed ? "Removed." : LeftInPlace.WhyNothingHappened(removal.Refused, FolderRefusals.None);

        return new StepOutcome(
            step.Description,
            removal.Removed,
            removal.BytesReclaimed,
            removal.Refused,
            message,
            EntriesRemoved: removal.Took ? 1 : 0);
    }

    /// <summary>
    /// The stores inside <paramref name="paths"/> now, off the calling thread: the folders asked about
    /// can hold hundreds of thousands of entries, and the caller may be resuming on the UI thread.
    /// </summary>
    private static Task<List<string>> StoresInsideAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.Run(
            () => paths
                .SelectMany(path => MailStoreSearch.Under(path, WindowsFileSystem.Default, ct))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ct);

    private async Task<long> MeasureAllAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = ScanSize.Zero;

        foreach (var path in paths)
        {
            var measured = await scanner.MeasureFromDiskAsync(path, ct).ConfigureAwait(false);
            total += measured.Size;
        }

        return total.Reclaimable;
    }
}
