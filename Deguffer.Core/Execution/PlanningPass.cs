using System.Runtime.ExceptionServices;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// What every plan of one pass is held to: the storage the registering rows name, the other account's
/// folders, and the age guard.
/// </summary>
/// <param name="Registrars">Every row whose registered storage the pass reads, a row not planned in it included.</param>
internal readonly record struct PassTerms(IReadOnlyList<ICleanupProvider> Registrars, AnotherAccountPlan? AnotherAccount, MinimumAge Keep);

/// <summary>
/// One planning pass's rows, planned several at once, each held to the pass's terms and reported as it
/// lands. <see cref="CleanupPlanner"/> prepares the pass and ends what it started. This plans in it.
///
/// <para><b>A row plans before the registered storage has been read, and is reported only after.</b> The
/// reading can start a tool, and no row needs the set until its own plan has ended, so read first, every
/// row would wait for the slowest registrar. A row whose plan has ended waits for the set out of its slot,
/// so it does not hold up the rows after it. There is one reading for the pass, so every plan in it is
/// held to the same set.</para>
///
/// <para><b>A failure is thrown by whoever owns the loop it happened in.</b> One in a row's slot ends the
/// work the pass started (<see cref="PassStart.CancelAsync"/>), and the slots' loop stops its other slots
/// itself and keeps that failure. Cancelling the loop's own token as well would lose it: a loop stopped by
/// its token says only that it was stopped. A failure outside the slots, in the reading or while a row's
/// plan is held to it, has no loop to keep it, so it stops the slots too, and <see cref="RunAsync"/>
/// throws it.</para>
/// </summary>
internal sealed class PlanningPass : IDisposable
{
    private readonly IReadOnlyList<ICleanupProvider> _providers;
    private readonly PassTerms _terms;
    private readonly IVolumeInventory _volumes;
    private readonly PassClock _clock;
    private readonly PassStart _start;

    // Ended by the caller, or by a failure outside the slots. Never by a failure in a slot, for the reason
    // the class gives.
    private readonly CancellationTokenSource _slots;

    // In the providers' order, whatever order they finish in, so the sort breaks a tie the same way on
    // every pass.
    private readonly Finding[] _findings;

    // Each row's plan held to the pass's terms and reported. Null for a row that never planned.
    private readonly Task?[] _holding;

    private readonly Lock _reporting = new();

    /// <param name="ct">The caller's, which ends the slots when it is cancelled.</param>
    public PlanningPass(
        IReadOnlyList<ICleanupProvider> providers,
        PassTerms terms,
        IVolumeInventory volumes,
        PassClock clock,
        PassStart start,
        CancellationToken ct)
    {
        _providers = providers;
        _terms = terms;
        _volumes = volumes;
        _clock = clock;
        _start = start;
        _slots = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _findings = new Finding[providers.Count];
        _holding = new Task?[providers.Count];
    }

    /// <summary>
    /// Starts the reading of the registered storage, plans every row, up to
    /// <see cref="CleanupPlanner.ProvidersAtOnce"/> at once, and returns the findings largest first.
    /// Throws the failure that ended the pass, not the stop it caused.
    /// </summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(
        IProgress<string>? status, IProgress<Finding>? found, IProgress<ProviderPlanTime>? timed)
    {
        // Before any slot is handed out, on the planner's thread, as PassStart asks.
        var registered = _start.Begin(ReadRegisteredAsync);

        var checking = new PlanningStatus(status);
        var options = new ParallelOptions { MaxDegreeOfParallelism = CleanupPlanner.ProvidersAtOnce, CancellationToken = _slots.Token };

        var planning = Parallel.ForEachAsync(
            Enumerable.Range(0, _providers.Count),
            options,
            (index, token) => PlanInSlotAsync(index, registered, checking, found, timed, token));

        await planning.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Until every row that planned has been held and reported, however the slots ended, so nothing
        // reports after the pass has returned.
        var held = Task.WhenAll(_holding.OfType<Task>());
        await held.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // A failure, not the stop it caused, is what the caller is owed, and a row that failed in its slot
        // may be recorded after a row its failure stopped.
        if (planning.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } failure)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        // Awaited whether or not any row held a plan to it: a pass of absent rows awaited nothing, and a
        // pass whose set could not be read must still fail.
        await registered.ConfigureAwait(false);
        await held.ConfigureAwait(false);

        // A pass stopped with nothing failed throws its cancellation.
        await planning.ConfigureAwait(false);

        return [.. _findings.OrderByDescending(finding => finding.EstimatedBytes)];
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>
    /// The pass's one reading of the registered storage. One that fails ends the pass at once, as a row
    /// whose plan fails does: no plan may be applied without the set, so the rows still planning would plan
    /// for nothing. One stopped because the pass is ending stops nothing else, so the failure that ended it
    /// is the one reported.
    /// </summary>
    private async Task<RegisteredStorage> ReadRegisteredAsync(CancellationToken ct)
    {
        var read = false;

        try
        {
            var registered = await RegisteredStorage.ReadAsync(_terms.Registrars, _volumes, ct).ConfigureAwait(false);
            read = true;
            return registered;
        }
        finally
        {
            if (!read)
            {
                await EndFromOutsideTheSlotsAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PlanInSlotAsync(
        int index,
        Task<RegisteredStorage> registered,
        PlanningStatus checking,
        IProgress<Finding>? found,
        IProgress<ProviderPlanTime>? timed,
        CancellationToken token)
    {
        var provider = _providers[index];

        UnheldPlan own;
        var planned = false;
        var began = _clock.Now;
        checking.Started(provider);

        try
        {
            own = await PlanOwnAsync(provider, _terms.Keep, checking.Counted(provider), token).ConfigureAwait(false);
            planned = true;
        }
        finally
        {
            // A provider that failed or was stopped is no longer being checked, and the line must not go
            // on naming it while the rest of the pass ends.
            checking.Ended(provider);

            // A failure ends the whole pass at once, the work started at its top included. The loop stops
            // only the slots, and a plan waiting on that work would hold the pass open until it answered.
            if (!planned)
            {
                await _start.CancelAsync().ConfigureAwait(false);
            }
        }

        // Read here, so a row's time is its own plan: neither its wait for the registered storage nor its
        // wait for another row's report.
        _holding[index] = HoldAndReportAsync(index, own, registered, new ProviderPlanTime(provider, began, _clock.Now), found, timed);
    }

    private async Task HoldAndReportAsync(
        int index,
        UnheldPlan own,
        Task<RegisteredStorage> registered,
        ProviderPlanTime time,
        IProgress<Finding>? found,
        IProgress<ProviderPlanTime>? timed)
    {
        var reported = false;

        try
        {
            var finding = new Finding(
                time.Provider,
                own.Present,
                own.Plan is null ? null : HoldToTerms(own.Plan, await registered.ConfigureAwait(false)),
                own.AwaitingFolders);
            _findings[index] = finding;

            // One at a time, so a caller's progress handler never runs beside itself: the preview's
            // inserts each row into a list that is not safe to change from two threads at once.
            lock (_reporting)
            {
                timed?.Report(time);
                found?.Report(finding);
            }

            reported = true;
        }
        finally
        {
            if (!reported)
            {
                await EndFromOutsideTheSlotsAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Ends the pass for a failure outside the slots: the work it started, and the slots. Nothing, where
    /// the pass is already ending, because the failure stopped here is one the ending caused.
    /// </summary>
    private async Task EndFromOutsideTheSlotsAsync()
    {
        if (_start.Token.IsCancellationRequested)
        {
            return;
        }

        await _start.CancelAsync().ConfigureAwait(false);
        await _slots.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The row's own presence and plan. A provider with nowhere approved to look is still asked for its
    /// plan, because that plan is where it says which folder to add. Short-circuiting on presence alone
    /// left that sentence unreachable: the one provider whose absence the user can do something about was
    /// the one that never got to say so.
    /// </summary>
    private static async Task<UnheldPlan> PlanOwnAsync(
        ICleanupProvider provider, MinimumAge keep, IProgress<ScanSize> counted, CancellationToken ct)
    {
        var present = await provider.IsPresentAsync(ct).ConfigureAwait(false);
        var awaitingFolders = provider.IsAwaitingSourceFolders;

        return !present && !awaitingFolders
            ? new UnheldPlan(Present: false, Plan: null, AwaitingFolders: false)
            : new UnheldPlan(present, await provider.PlanAsync(keep, counted, ct).ConfigureAwait(false), awaitingFolders);
    }

    /// <summary>
    /// <paramref name="plan"/> held to the terms that are the pass's rather than the row's. The set is
    /// gathered from every row that registers storage, and no row can see another's records (see
    /// <see cref="VirtualMachineStoragePlan"/>). Whose account this process runs as is the run's, and no
    /// row should have to remember to ask (see <see cref="AnotherAccountPlan"/>).
    /// </summary>
    private CleanupPlan HoldToTerms(CleanupPlan plan, RegisteredStorage registered)
    {
        var held = VirtualMachineStoragePlan.Apply(plan, registered, _volumes);

        return _terms.AnotherAccount is { } anotherAccount ? anotherAccount.Apply(held) : held;
    }

    /// <summary>
    /// What a row planned on its own, before the pass's terms are applied to it. A type of its own, so a
    /// plan that has not yet been held to the registered storage can never be reported as a
    /// <see cref="Finding"/> (§5.2).
    /// </summary>
    private readonly record struct UnheldPlan(bool Present, CleanupPlan? Plan, bool AwaitingFolders);
}
