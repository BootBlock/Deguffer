using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// How <see cref="PlanExecutor"/> carries out an <see cref="EmptyRecycleBinStep"/>: one volume's bin is
/// handed to Windows, and the disk afterwards says what that achieved.
///
/// <para><b>Never defaulted to the real emptier.</b> It empties the Recycle Bin of whoever runs the
/// process, so a test that forgot to pass a fake would be one broken guard away from doing that. The
/// real one is given only where the app composes its providers, <see cref="CleanupPlanner.CreateDefault"/>.
/// The provider that plans the step chooses its route by asking the same emptier, so a plan cannot
/// choose a route on one object's answer and then be carried out by another.</para>
/// </summary>
internal sealed class RecycleBinRun(IRecycleBinEmptier emptier) : StepRoute<EmptyRecycleBinStep>
{
    /// <summary>
    /// Hand one volume's bin to Windows, then find out what that achieved by looking at the disk.
    ///
    /// <para><b>The reclaim is measured rather than assumed.</b> <c>SHEmptyRecycleBin</c> reports
    /// one HRESULT and no figures at all, so an estimate reported as a result would be a number
    /// nobody checked — and the estimate is a plan-time measurement of a directory anything on the
    /// machine may have written to since. Subtracting a fresh reading of the same path is the same
    /// arithmetic <see cref="CommandRun"/> does after a §5.1 command, for the same reason, and
    /// it costs almost nothing here: the directory it re-measures is the one just emptied.</para>
    ///
    /// <para>The measurement comes from the disk rather than the volume snapshot, which is what
    /// makes it a second reading instead of the first one handed back. See
    /// <see cref="CommandRun"/>, where that was found.</para>
    ///
    /// <para>Windows leaves the account's directory standing and empty rather than removing it,
    /// which was observed rather than assumed, so nothing here reads its absence as success.</para>
    /// </summary>
    private protected override async Task<StepOutcome> CarryOutAsync(StepRun run, EmptyRecycleBinStep step, CancellationToken ct)
    {
        // The guard and this route are mutually exclusive, and RecycleBinProvider is what makes
        // them so. Re-asked here because the cost of that one expression being edited wrongly is
        // the files the user explicitly asked to keep: Windows empties a bin whole and has no way
        // to hold anything back, so this step under a guard would destroy them and §5.6 would not
        // notice, since they sit inside the target rather than beside it.
        if (run.Keep.IsOn)
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
        // deleted into it since the preview, or inside a folder Windows will not list, would go with
        // everything else. Looked for on the disk, because the plan was made before it arrived.
        var look = await run.Clock.LookAsync([step.Path], MinimumAge.Off, ct).ConfigureAwait(false);

        if (look.WhyNot("Nothing was removed", "Windows empties a Recycle Bin whole", MinimumAge.Off) is { } held)
        {
            return new StepOutcome(
                step.Description, Succeeded: false, BytesReclaimed: 0, Refusals.None, held, MailStores: look.Stores.Count);
        }

        // The last honest moment to stop: the call itself cannot be cancelled once it starts, and
        // on a large bin it runs for a long time. See ShellRecycleBinEmptier.
        ct.ThrowIfCancellationRequested();

        var outcome = await Task.Run(() => emptier.Empty(step.VolumeRoot), ct).ConfigureAwait(false);

        var after = await DiskReading.ReadAsync(run.Run.Scanner, [step.Path], ScanBounds.Contents, ct).ConfigureAwait(false);

        // Nothing to report along the way — the shell offers no progress of its own, and its
        // progress window is one of the three things the flags suppress.
        run.Progress?.Report(1.0);

        // No evidence either way, so Windows' own answer is the only thing to report and nothing is
        // credited. Taking the zero instead would report the whole bin as reclaimed.
        if (after.Unreached is not null)
        {
            return new StepOutcome(
                step.Description,
                outcome.Emptied,
                BytesReclaimed: 0,
                Refusals.None,
                $"{outcome.Message ?? "Windows reported the bin emptied."} {after.WhyNothingIsCounted}.");
        }

        var remaining = after.Size.Reclaimable;
        var reclaimed = step.EstimatedBytes - remaining;

        // The entries by the same subtraction. The bin's own folder stays standing and is in neither
        // count, because both readings keep it.
        var entriesRemoved = Math.Max(0, step.Estimated.Entries - after.Size.Entries);

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
            EntriesRemoved: entriesRemoved,
            Unreleased: succeeded && reclaimed > 0 ? remaining : 0);
    }
}
