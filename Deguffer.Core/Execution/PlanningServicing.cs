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
/// under way, which would hold the driver store, Windows.old, the setup files and an update's leftovers
/// on every elevated pass, and the component store's own rows before they read the analysis. The
/// component store's hold reads this before DISM starts, so the pass is planned against the machine as
/// it was before Deguffer touched it.</para>
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

    /// <summary>Forgets the last pass's reading, for a planning pass that starts afresh.</summary>
    public void Invalidate() => _state.Forget();
}
