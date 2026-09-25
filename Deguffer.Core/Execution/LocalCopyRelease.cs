using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out a <see cref="ReleaseLocalCopiesStep"/>. Apart from the
/// removals because nothing about it is a removal: it destroys nothing, measures nothing afterwards, and
/// reports a request rather than a reclaim.
/// </summary>
internal static class LocalCopyRelease
{
    /// <summary>
    /// How often a release reports progress, in files: often enough to move the bar, and far less often
    /// than the hundred thousand posts to the UI thread a large account would otherwise make.
    /// </summary>
    private const int ProgressInterval = 256;

    /// <summary>
    /// Ask a sync app to release each file the plan named that still meets the rules, and report what
    /// was asked for.
    ///
    /// <para><b>Nothing is measured afterwards, and nothing is reported as reclaimed.</b> The sync app
    /// releases a file when it chooses to, so a reading taken here would describe a moment and not the
    /// outcome. The run says what it asked for, and the free space the shell measures afterwards says
    /// what had happened by then.</para>
    ///
    /// <para><b>The sync app is asked about first.</b> One that is not running records the request and
    /// releases nothing, so the run would report a figure nothing will act on. It is asked now rather
    /// than trusted from the preview, because the app can be closed in between.</para>
    /// </summary>
    public static async Task<StepOutcome> RunAsync(
        ICloudFiles cloud,
        ReleaseLocalCopiesStep step,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        if (cloud.ProviderState(step.SyncRoot) is not SyncProviderState.Running)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                $"Nothing was asked: {step.SyncApp} is not running, so nothing would be released. "
                + "Start it and scan again.");
        }

        // Once, so every file is checked against where the root really is, and a root reached through a
        // link of the user's own still resolves.
        if (cloud.Resolve(step.SyncRoot) is not { } resolvedRoot)
        {
            return new StepOutcome(
                step.Description,
                Succeeded: false,
                BytesReclaimed: 0,
                Refusals.None,
                $"Nothing was asked: Windows would not open {LongPath.Display(step.SyncRoot)}.");
        }

        var tally = await Task.Run(() => Request(cloud, step, resolvedRoot, keep, progress, ct), ct).ConfigureAwait(false);

        progress?.Report(1.0);

        var asked = tally.GetValueOrDefault(ReleaseResult.Requested);
        var changed = tally.GetValueOrDefault(ReleaseResult.NoLongerEligible).Files;
        var gone = tally.GetValueOrDefault(ReleaseResult.Gone).Files;
        var refused = tally.GetValueOrDefault(ReleaseResult.Refused).Files;

        var said = asked.Files > 0
            ? $"Asked {step.SyncApp} to release {FreeSpace.Format(asked.Bytes)} held in {asked.Files:N0} file(s). "
              + "It does that in the background, and Deguffer cannot see when."
            : "Nothing was asked.";

        var message = said
            + (changed > 0 ? $" {changed:N0} file(s) had changed since the scan and were left as they were." : string.Empty)
            + (gone > 0 ? $" {gone:N0} file(s) were no longer there." : string.Empty)
            + (refused > 0 ? $" Windows would not let Deguffer ask about {refused:N0} file(s)." : string.Empty);

        return new StepOutcome(
            step.Description,
            Succeeded: asked.Files > 0 || refused == 0,
            BytesReclaimed: 0,
            Refusals.None,
            message,
            BytesRequested: asked.Bytes);
    }

    /// <summary>
    /// The requests themselves, off the calling thread. Every file is judged again by
    /// <see cref="ReleaseRules.Hold"/> through the handle that unpins it, under the pin its folders pass
    /// on now and the guard fixed when the plan was made.
    /// </summary>
    private static Dictionary<ReleaseResult, HeldTally> Request(
        ICloudFiles cloud,
        ReleaseLocalCopiesStep step,
        string resolvedRoot,
        MinimumAge keep,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var pins = new InheritedPins(cloud, step.SyncRoot);
        var tally = new Dictionary<ReleaseResult, HeldTally>();

        for (var i = 0; i < step.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = step.Files[i];
            var inherited = Path.GetDirectoryName(LongPath.Display(file.Path)) is { } folder
                ? pins.For(folder)
                : PinState.Pinned;

            var resolved = Path.Join(
                resolvedRoot, Path.GetRelativePath(LongPath.Display(step.SyncRoot), LongPath.Display(file.Path)));
            var answer = cloud.Release(file.Path, resolved, now => ReleaseRules.Hold(now, inherited, keep) is null);

            tally[answer.Result] = tally.GetValueOrDefault(answer.Result) + answer.RequestedBytes;

            if (i % ProgressInterval == 0)
            {
                progress?.Report((double)i / step.Files.Count);
            }
        }

        return tally;
    }
}
