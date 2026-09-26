using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The component store's own cleanup: <c>DISM /StartComponentCleanup</c>, which removes the older
/// versions of Windows components that updates have replaced. See <see cref="ComponentStoreProviderBase"/>
/// for the plan both component store rows share.
///
/// <para><b>DISM rather than the scheduled task.</b> Windows' <c>StartComponentCleanup</c> task does the
/// same work, but waits 30 days after a component is updated and stops after an hour, so a run through it
/// can free nothing and does not say so. DISM removes the older versions at once and runs to the end,
/// which is what makes its before-and-after figures describe what the clean did.</para>
///
/// <para><b>Tier 2.</b> What goes is Windows' rollback copy of each replaced component, kept for the
/// 30 days in which it removes them itself. Nothing Windows runs is removed, and every installed update
/// can still be uninstalled; that cost belongs to <see cref="ComponentStoreResetBaseProvider"/>.</para>
/// </summary>
public sealed class ComponentStoreCleanupProvider(
    IUserEnvironment? environment = null,
    IProcessRunner? runner = null,
    IProcessInspector? inspector = null,
    ISystemDirectories? system = null,
    IWindowsServicing? servicing = null,
    ComponentStoreAnalysis? analysis = null)
    : ComponentStoreProviderBase(environment, runner, inspector, system, servicing, analysis)
{
    public override string Id => "component-store";

    public override string Name => "Superseded Windows components";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Windows removes the older versions of its components that updates have replaced now, rather than "
        + "30 days after each update, so it can no longer roll those components back. Every installed update "
        + "can still be uninstalled. The cleanup can take many minutes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows' component store (WinSxS)",
        Publisher = "Microsoft",
        Purpose = "Windows keeps every version of every system component it has installed in its component "
            + "store, so it can roll an update back. It removes the older versions itself about a month after "
            + "each update, and only when the machine is idle, so the store can hold several gigabytes of them.",
        Recommendation = "Removed by Windows' own component cleanup, which decides for itself which versions are "
            + "no longer needed. Deguffer never deletes from the store, which Microsoft warns can stop Windows "
            + "starting or updating.",
    };

    protected override string CleanupArguments => "/StartComponentCleanup";

    protected override string What =>
        "Remove the Windows components that updates have replaced, using Windows' own component cleanup";
}
