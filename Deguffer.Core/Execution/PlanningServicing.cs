using System.Runtime.CompilerServices;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Execution;

/// <summary>
/// What Windows servicing was doing when a planning pass first asked, which every row the update hold
/// governs is planned against for the rest of the pass.
///
/// <para><b>Why one reading for the pass.</b> An administrator's pass starts DISM's analysis of the
/// component store as it begins (see <see cref="ComponentStoreProviderBase.BeginPass"/>), and for as
/// long as DISM runs, Windows runs its servicing stack and keeps
/// <see cref="RestartKey.ServicingRebootPending"/> present. Read live after that, both say an update is
/// under way, and the component store's own rows would be held before they read the analysis. The
/// component store's hold reads this before DISM starts, so the pass is planned against the machine as
/// it was before Deguffer touched it. The other rows' <see cref="Hold"/> does not count the stack an
/// earlier pass's analysis left running either.</para>
///
/// <para><b>Safe because the clean asks again.</b> Every step these rows plan is
/// <see cref="CleanupStep.HeldWhileUpdating"/>, so <see cref="PlanExecutor"/> asks the machine itself
/// immediately before the step runs. An update that starts while the preview is made is held there,
/// and the preview offers no more than the clean will check.</para>
///
/// <para><b>Shared by its sources.</b> Every provider built on the same servicing and process seams
/// is handed the same reading, as the app's providers are on the machine's own, so no row can read the
/// machine later in the pass than another. A test's fakes get a reading of their own.</para>
/// </summary>
public sealed class PlanningServicing
{
    private static readonly ConditionalWeakTable<IWindowsServicing, ConditionalWeakTable<IProcessInspector, PlanningServicing>> BySource = new();

    private readonly IWindowsServicing _servicing;
    private readonly IProcessInspector _inspector;
    private readonly PassMemo<ServicingState> _state = new();

    private PlanningServicing(IWindowsServicing servicing, IProcessInspector inspector)
    {
        _servicing = servicing;
        _inspector = inspector;
    }

    /// <summary>The one reading every provider built on <paramref name="servicing"/> and <paramref name="inspector"/> shares.</summary>
    public static PlanningServicing For(IWindowsServicing servicing, IProcessInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(servicing);
        ArgumentNullException.ThrowIfNull(inspector);

        return BySource
            .GetValue(servicing, _ => new ConditionalWeakTable<IProcessInspector, PlanningServicing>())
            .GetValue(inspector, _ => new PlanningServicing(servicing, inspector));
    }

    /// <summary>The machine as this pass first asked about it.</summary>
    public ServicingState State => _state.Get(() => ServicingState.Read(_servicing, _inspector));

    /// <summary>
    /// Why nothing an update left may be offered this pass, or null where nothing holds it.
    ///
    /// <para>The servicing stack a component store command of Deguffer's own woke is not counted: a
    /// pass started straight after another meets the stack the last pass's analysis woke, which Windows
    /// keeps running for minutes. Every step offered on this answer must be
    /// <see cref="CleanupStep.HeldWhileUpdating"/>, because <see cref="UpdateHoldAtClean"/> then waits
    /// for that stack to stop before the step runs. A row offered while it runs says so with
    /// <see cref="WaitsForOwnServicing"/>.</para>
    /// </summary>
    public string? Hold => UnfinishedUpdate.HoldsEverything(State, OwnServicingCalls.For(_servicing));

    /// <summary>
    /// The plan's note where only Deguffer's own servicing session is running this pass, so a row
    /// offered by <see cref="Hold"/> says why its clean may wait, or null.
    /// </summary>
    public PlanNote? WaitsForOwnServicing =>
        UnfinishedUpdate.OnlyOwnServicing(State, OwnServicingCalls.For(_servicing))
            ? new PlanNote(PlanNoteSeverity.Information, UnfinishedUpdate.WaitsForOwnServicing)
            : null;

    /// <summary>Forgets the last pass's reading, for a planning pass that starts afresh.</summary>
    public void Invalidate() => _state.Forget();
}
