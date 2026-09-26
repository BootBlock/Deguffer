using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The component store's reset: <c>DISM /StartComponentCleanup /ResetBase</c>, which removes every
/// superseded version of every component, and with them the means to uninstall any update installed so
/// far. See <see cref="ComponentStoreProviderBase"/> for the plan both component store rows share.
///
/// <para><b>A row of its own, never a switch on the cleanup.</b> Microsoft's warning is that "all
/// existing update packages can't be uninstalled after this command is completed". That is a permanent
/// cost, so it is put in front of the user as its own decision and confirmed on its own, rather than
/// carried by a row the user may already have chosen for its space.</para>
///
/// <para><b>Tier 3.</b> Nothing is deleted that a user made, but what is lost cannot be had back: an
/// update that turns out to break something can no longer be removed, and the only way out is to reset or
/// reinstall Windows. That is §3's "gone permanently", which is what Tier 3 exists to say.</para>
/// </summary>
public sealed class ComponentStoreResetBaseProvider(
    IUserEnvironment? environment = null,
    IProcessRunner? runner = null,
    IProcessInspector? inspector = null,
    ISystemDirectories? system = null,
    IWindowsServicing? servicing = null,
    ComponentStoreAnalysis? analysis = null)
    : ComponentStoreProviderBase(environment, runner, inspector, system, servicing, analysis)
{
    public override string Id => "component-store-reset-base";

    public override string Name => "Windows update uninstall data";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "No update installed so far can be uninstalled afterwards, from Settings or anywhere else, so an update "
        + "that turns out to cause a problem can only be undone by resetting or reinstalling Windows. Updates "
        + "installed later can still be uninstalled. The cleanup can take many minutes.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows' component store (WinSxS)",
        Publisher = "Microsoft",
        Purpose = "Besides the older versions its ordinary cleanup removes, Windows keeps what it needs to "
            + "uninstall each update it has installed. Resetting the store's base removes every superseded "
            + "version, and with it the means to uninstall any update installed so far.",
        Recommendation = "Only once the updates on this machine have proved themselves, and only if the space "
            + "matters more than being able to remove one. Windows does the reset with its own command, and "
            + "Deguffer never deletes from the store.",
    };

    protected override string CleanupArguments => "/StartComponentCleanup /ResetBase";

    protected override string What =>
        "Remove every superseded Windows component, and with them the means to uninstall any update installed "
        + "so far, using Windows' own component cleanup";
}
