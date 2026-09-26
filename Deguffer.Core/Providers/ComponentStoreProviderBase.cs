using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Windows component store, <c>C:\Windows\WinSxS</c>, reclaimed by DISM's own cleanup and never by
/// deleting a path. The two rows over it differ only in the command and what it costs:
/// <see cref="ComponentStoreCleanupProvider"/> and <see cref="ComponentStoreResetBaseProvider"/>.
///
/// <para><b>§5.1, because nothing else is safe.</b> Microsoft's own warning is that deleting from the
/// store "may severely damage your system so that your PC might not boot and make it impossible to
/// update". The store is protected from every other provider by <see cref="WindowsSystemRoot"/>, and
/// here it is never a target either: the plan is one DISM command, and DISM decides what goes.</para>
///
/// <para><b>Windows' figures, not a measurement.</b> Most of the store is hard-linked into Windows
/// itself, so a walk of it overstates what any cleanup frees many times over. DISM's analysis counts
/// the shared files once and says what is overhead, and the run asks it again before and after the
/// command, so the reclaim is Windows' own before-and-after pair.</para>
///
/// <para><b>Held while Windows is updating</b>, at planning and again when the run reaches it: DISM
/// servicing the store while the servicing stack is busy, or while a restart is owed, is the one time
/// it can leave the store inconsistent.</para>
/// </summary>
public abstract class ComponentStoreProviderBase : CleanupProviderBase
{
    private readonly ComponentStoreAnalysis _analysis;
    private readonly string _store;
    private readonly string _windows;

    protected ComponentStoreProviderBase(
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        ISystemDirectories? system,
        IWindowsServicing? servicing,
        ComponentStoreAnalysis? analysis)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            DirectoryScanner.Default,
            servicing: servicing)
    {
        system ??= SystemDirectories.Current;
        _analysis = analysis ?? new ComponentStoreAnalysis(system, Runner);
        _windows = system.WindowsDirectory;
        _store = Path.Combine(_windows, "WinSxS");
    }

    /// <summary>
    /// The store's overhead, which both rows offer and either command frees from, so a preview totals
    /// it once however many of the rows are chosen.
    /// </summary>
    public static readonly ReclaimPool Overhead = new("windows-component-store");

    public override StepGrain Grain => StepGrain.Parts;

    /// <summary>What DISM is told to do, after <c>/Online /English</c>.</summary>
    protected abstract string CleanupArguments { get; }

    /// <summary>What the step says it does.</summary>
    protected abstract string What { get; }

    public override void InvalidateCaches()
    {
        _analysis.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>The store's folder. DISM is part of every Windows that has one.</summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(_store));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (NothingToPlanFor(_store, "This Windows keeps no component store.") is { } nothing)
        {
            return nothing;
        }

        // Asked before DISM is, because the analysis waits on the servicing stack while it works.
        if (UnfinishedUpdate.HoldsEverything(Servicing, Inspector) is { } updating)
        {
            return EmptyPlan(updating) with { ProtectedPaths = [UnfinishedUpdate.Held(_store)] };
        }

        var reading = await _analysis.ReadingAsync(ct).ConfigureAwait(false);

        if (reading.NeedsElevation)
        {
            return UnexaminedPlan(
                "Windows analyses its component store only for an administrator, and only an administrator "
                + "can clean it. Scanning as administrator lets Deguffer ask.");
        }

        if (reading.Report is not { } report)
        {
            return UnexaminedPlan(
                $"Windows did not analyse its component store: {reading.Failure} Nothing is offered rather "
                + "than guessed at.");
        }

        var analysed = new PlanNote(PlanNoteSeverity.Information, Arithmetic(report));

        if (!report.CleanupRecommended || report.Overhead == 0)
        {
            var clear = EmptyPlan("Windows reports that its component store does not need cleaning.");
            return clear with { Notes = [.. clear.Notes, analysed] };
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps =
            [
                new RunCommandStep(_analysis.Dism, $"/Online /English /Cleanup-Image {CleanupArguments}", What)
                {
                    // The most a cleanup could free, which Windows does not narrow down further, so it is
                    // shown as the prediction it is. The reclaim is measured, not taken from this.
                    Estimated = ScanSize.Approximate(report.Overhead),
                    MeasuredBy = _analysis,
                    SharesReclaim = Overhead,
                    RequiresElevation = true,
                    HeldWhileUpdating = true,
                },
            ],
            ProtectedPaths = Protect(
                (_store, "The component store itself. Windows' cleanup removes superseded components inside it, and never the store."),
                (Path.Combine(_windows, "servicing", "Packages"),
                    "Windows' record of the updates installed on this machine. The cleanup removes superseded entries from it, and never the record."),
                (Path.Combine(_windows, "System32"),
                    "Windows itself, which shares most of the component store's files through hard links.")),
            Notes =
            [
                analysed,
                new PlanNote(
                    PlanNoteSeverity.Information,
                    "The figure is the most a cleanup could free, as Windows counts it. Windows removes superseded "
                    + "components and temporary data, never the payload of a feature that is switched off, so the "
                    + "clean usually frees less. It reports what Windows measures afterwards."),
                new PlanNote(
                    PlanNoteSeverity.Information,
                    "Windows gives this cleanup no time limit, and it can take many minutes. Once it starts, it "
                    + "finishes even if Deguffer stops waiting for it."),
            ],
        };
    }

    /// <summary>
    /// Microsoft's own arithmetic, which is the answer to the question the store's size in Explorer
    /// raises: most of that size is Windows itself, counted twice.
    /// </summary>
    private static string Arithmetic(ComponentStoreReport report) =>
        $"Explorer counts the component store as {FreeSpace.Format(report.ExplorerSize)}, but "
        + $"{FreeSpace.Format(report.SharedWithWindows)} of it is shared with Windows itself, so Windows reports "
        + $"the store's actual size as {FreeSpace.Format(report.ActualSize)}. Of that, "
        + $"{FreeSpace.Format(report.BackupsAndDisabledFeatures)} is superseded components and switched-off "
        + $"features, and {FreeSpace.Format(report.CacheAndTemporaryData)} is cache and temporary data. Windows "
        + $"counts {report.ReclaimablePackages} superseded {(report.ReclaimablePackages == 1 ? "package" : "packages")} "
        + "it can remove"
        + (report.LastCleanup is { } last ? $", and last cleaned the store on {last}." : ".");
}
