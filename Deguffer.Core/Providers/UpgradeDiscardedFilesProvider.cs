using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The files a Windows upgrade set aside rather than carry over, in <c>$WINDOWS.~Q</c>,
/// <c>$INPLACE.~TR</c> and <c>$Windows.~LS</c> at the top of the system drive.
///
/// <para><b>Windows' own registration is the whole of what is known.</b> The <em>Upgrade Discarded
/// Files</em> handler is <c>setupcln.dll</c>, the class behind <see cref="PreviousWindowsInstallationProvider"/>,
/// and its key under <c>VolumeCaches</c> names exactly these three in <c>SetupDirectories</c>, read on
/// Windows 11 24H2. Microsoft documents none of the three folders. The handler's own description
/// (<c>setupcln.dll</c>, string 1005) is: "As a precaution, Windows upgrade keeps a copy of any files
/// that were not moved to the new version of Windows and were not identified as Windows system files.
/// If you are sure that no user's personal files are missing after the upgrade, you can delete these
/// files."</para>
///
/// <para><b>Tier 3, not Tier 2 beside <c>Windows.old</c>.</b> What Windows keeps here is, by its own
/// description, whatever it could not place, which is the one part of an upgrade most likely to be
/// somebody's own files, and the copy here may be the only one. Nothing re-creates it, and §3 puts
/// anything whose loss is permanent in Tier 3. So it is its own row, never pre-selected, and confirmed
/// as a permanent loss, where a fourth row in the Tier 2 provider would have offered it on that
/// provider's terms.</para>
///
/// <para>Removed through the handler, and only once the upgrade can no longer be undone and no update
/// is unfinished, for the reasons <see cref="SetupCleanupProviderBase"/> gives: the uninstall window is
/// also the time somebody has to notice that something of theirs did not come across.</para>
/// </summary>
public sealed class UpgradeDiscardedFilesProvider : SetupCleanupProviderBase
{
    /// <summary>
    /// The one registration, with its directories in the order its <c>SetupDirectories</c> value lists
    /// them, spelled as it spells them.
    /// </summary>
    internal static readonly IReadOnlyList<SetupCleanup> Cleanups =
    [
        new(
            "Upgrade Discarded Files",
            ["$WINDOWS.~Q", "$INPLACE.~TR", "$Windows.~LS"],
            "Files a Windows upgrade did not move to the new version and did not recognise as part of "
            + "Windows, kept as a precaution. Some of them may be your own, and this may be the only "
            + "copy."),
    ];

    public UpgradeDiscardedFilesProvider(
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

    public override string Id => "upgrade-discarded-files";

    public override string Name => "Files discarded by a Windows upgrade";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "Every file a Windows upgrade set aside instead of moving is gone for good, including any of "
        + "your own among them. Windows itself keeps working exactly as it is now.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Windows,
        Application = "Windows Setup",
        Publisher = "Microsoft",
        Purpose = "When Windows upgrades, it keeps a copy of any file it did not move to the new "
            + "version and did not recognise as part of Windows, as a precaution, in folders at the top "
            + "of the system drive.",
        Recommendation = "Windows' own advice is to delete these only once you are sure none of your "
            + "personal files went missing in the upgrade. Offered only once the upgrade can no longer "
            + "be undone, and removed by Windows' own cleanup rather than by deleting the folders.",
    };

    protected override string NothingLeftBehind => "This drive holds no files a Windows upgrade set aside.";

    protected override string WhileTheUpgradeCanBeUndone(int windowDays) =>
        $"the upgrade that set these files aside can be undone for {windowDays} days after it, and that "
        + "is also the time to check that nothing of yours went missing. Deguffer offers them after that.";
}
