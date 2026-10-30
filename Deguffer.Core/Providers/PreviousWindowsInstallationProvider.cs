using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Windows upgrade leaves at the top of the system drive once it has finished: the previous
/// installation in <c>Windows.old</c>, Setup's working folder <c>$Windows.~BT</c>, and the
/// installation files it downloaded into <c>$Windows.~WS</c> and <c>ESD</c>. <c>Windows.old</c> alone is
/// routinely tens of gigabytes, and on a recently upgraded machine it is the largest reclaim there is.
///
/// <para><b>Tier 2.</b> None of it comes back except by upgrading again, and what deleting it costs is
/// a named capability rather than a slower next use: going back to the previous version, and — by
/// Windows' own description of the installation files — resetting this PC from them.</para>
///
/// <para>Removed through Windows' own handlers, and only once the upgrade can no longer be undone and
/// no update is unfinished, for the reasons <see cref="SetupCleanupProviderBase"/> gives. Windows
/// removes all of this by itself when the uninstall window closes, and a folder it failed to remove
/// then is exactly what this is for.</para>
/// </summary>
public sealed class PreviousWindowsInstallationProvider : SetupCleanupProviderBase
{
    /// <summary>
    /// The handlers, and the directories each one's registration names, in the order a row is named
    /// after them. The handler's registration is Windows' statement of what it clears, and this table
    /// is that statement written down so it can be read and tested rather than discovered.
    /// </summary>
    internal static readonly IReadOnlyList<SetupCleanup> Cleanups =
    [
        new(
            "Previous Installations",
            ["Windows.old"],
            "The previous Windows installation, kept so the upgrade could be undone. Windows' own "
            + "cleanup removes it, together with the record that let Settings offer to go back."),
        new(
            "Temporary Setup Files",
            ["$Windows.~BT"],
            "Windows Setup's working folder from an upgrade that has finished."),
        new(
            "Windows ESD installation files",
            ["$Windows.~WS", Path.Combine("ESD", "Windows"), Path.Combine("ESD", "Download")],
            "The Windows installation files Setup downloaded for an upgrade that has finished."),
    ];

    public PreviousWindowsInstallationProvider(
        IDiskCleanupHandlers handlers,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        IWindowsServicing? servicing = null)
        : base(Cleanups, handlers, environment, runner, inspector, scanner, system, servicing)
    {
    }

    public override string Id => "previous-windows-installation";

    public override string Name => "Previous Windows installation";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Going back to the previous version of Windows is no longer possible, and anything an upgrade "
        + "left behind in the previous installation goes with it. Windows describes its downloaded "
        + "installation files as needed to reset this PC, so a reset afterwards needs Windows "
        + "downloaded again or installation media. Windows itself keeps working exactly as it is now.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "Windows Setup",
        Publisher = "Microsoft",
        Purpose = "Upgrading Windows moves the previous installation aside into Windows.old, so the "
            + "upgrade can be undone, and leaves Setup's working folder and the installation files it "
            + "downloaded beside it. Windows keeps them for ten days after an upgrade unless that has "
            + "been changed, then removes them itself, and sometimes it does not.",
        Recommendation = "Offered only once the upgrade can no longer be undone and no update is "
            + "waiting to finish, and removed by Windows' own cleanup rather than by deleting the "
            + "folders. Look inside Windows.old first if anything from before the upgrade is missing.",
    };

    protected override string NothingLeftBehind => "This drive holds nothing a finished Windows upgrade left behind.";

    protected override string WhileTheUpgradeCanBeUndone(int windowDays) =>
        $"Windows keeps it for {windowDays} days after an upgrade so the upgrade can be undone, and removes "
        + "it by itself when that time is up. Deguffer offers it if Windows has not.";
}
