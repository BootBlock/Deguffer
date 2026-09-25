using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Diagnostic logs tools write into the temporary folder and never trim: the Remote Desktop client's
/// automatic traces, Visual Studio ServiceHub's logs, and the VS Code updater's logs (569 MB of
/// Remote Desktop traces alone on the machine that prompted this; one report puts ServiceHub's past
/// 6 GB).
///
/// <para><b>Tier 3, because a log is a record and nothing rebuilds it.</b> Nothing reads these back
/// — Microsoft's own troubleshooting pages send a person to them by hand — but the day they are
/// wanted is the day after something went wrong, and they cannot be had again. So this row is never
/// ticked for you.</para>
///
/// <para><b>Only the folders the logs are written into are emptied, never the tool's folder.</b>
/// <c>DiagOutputDir</c> is a folder several Microsoft clients share, and Deguffer empties the two
/// log folders it knows inside it. What else is there is asserted to survive.</para>
///
/// <para><b>No process holds anything back.</b> A running client keeps its current log open, and
/// Windows refuses to delete a file that is open, which §5.3 treats as ordinary. The logs it has
/// finished with are what is offered, and a program that is writing one loses nothing it still has.
/// </para>
///
/// <para><b><c>VSTelem</c> and <c>VSTelem.Out</c> are not offered.</b> Visual Studio's
/// responsiveness monitoring creates them, but nothing published says what they hold or whether
/// anything reads them back, and a name is not enough to delete on.</para>
/// </summary>
public sealed partial class TempToolLogProvider : TempMarkerProviderBase
{
    private const string RemoteDesktopTool = "Remote Desktop client";
    private const string WindowsAppTool = "Windows App";
    private const string ServiceHubTool = "Visual Studio ServiceHub";
    private const string VsCodeUpdaterTool = "VS Code updater";

    /// <summary>What the VS Code updater's helper names its log: the time it ran, in seconds.</summary>
    [GeneratedRegex(@"\Avscode-inno-updater-[0-9]+\.log\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnoUpdaterLog();

    [GeneratedRegex(@"\ARdClientAutoTrace\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RdClientAutoTrace();

    [GeneratedRegex(@"\ALogs\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Logs();

    private static readonly TempMarker[] TopLevel =
    [
        new(
            VsCodeUpdaterTool,
            InnoUpdaterLog(),
            TargetKind.File,
            "The log the VS Code updater wrote while it replaced VS Code's files after an update."),
    ];

    private static readonly TempMarker[] DiagOutput =
    [
        new(
            RemoteDesktopTool,
            RdClientAutoTrace(),
            TargetKind.DirectoryContents,
            "Traces the Remote Desktop client records automatically, in case a connection has to be "
            + "diagnosed. Nothing reads them back unless you send them to support."),
    ];

    private static readonly TempMarker[] WindowsApp =
    [
        new(
            WindowsAppTool,
            Logs(),
            TargetKind.DirectoryContents,
            "Logs the Windows App writes about its connections, in case one has to be diagnosed."),
    ];

    private static readonly TempMarker[] ServiceHub =
    [
        new(
            ServiceHubTool,
            Logs(),
            TargetKind.DirectoryContents,
            "Logs the services behind Visual Studio and VS Code's C# tooling write, in case one of "
            + "them has to be diagnosed."),
    ];

    public TempToolLogProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null)
        : base(environment, runner, inspector, scanner, system, liveTrees)
    {
    }

    public override string Id => "temp-tool-logs";

    public override string Name => "Tool logs in temporary folders";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "Nothing changes for any application, and each writes new logs as before. What you lose is "
        + "the record of past connections, services and updates, which support may ask for after "
        + "something has gone wrong.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the Remote Desktop client, the Windows App, Visual Studio and VS Code",
        Publisher = "Microsoft",
        Purpose = "These write diagnostic logs into your temporary folder and do not limit how much "
            + "they keep, so the logs grow for as long as the application is used.",
        Recommendation = "Nothing reads these back, but they are the only record of what happened, "
            + "so they are offered and never ticked for you. Deguffer empties the folders the logs "
            + "are written into and leaves the folders themselves.",
    };

    protected override string NothingLeftBehind =>
        "None of these applications has left a log in a temporary folder.";

    protected override IReadOnlyList<TempMarkerPlace> PlacesIn(IReadOnlyList<string> accountFolders)
    {
        var places = new List<TempMarkerPlace>();

        foreach (var folder in accountFolders)
        {
            var diag = Path.Combine(folder, "DiagOutputDir");

            places.Add(new TempMarkerPlace(folder, TopLevel));

            // Innermost first, for the reason TempToolCacheProvider gives.
            places.Add(new TempMarkerPlace(Path.Combine(diag, "Windows365"), WindowsApp, WindowsAppTool, folder));
            places.Add(new TempMarkerPlace(diag, DiagOutput, "Microsoft's remote-connection clients", folder));
            places.Add(new TempMarkerPlace(Path.Combine(folder, "servicehub"), ServiceHub, ServiceHubTool, folder));
        }

        return places;
    }
}
