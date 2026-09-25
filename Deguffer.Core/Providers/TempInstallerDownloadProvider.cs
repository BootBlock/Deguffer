using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Installers that applications download into the temporary folder to update themselves, and the
/// per-session folders Blender leaves when it is killed: VS Code's and Docker Desktop's updater
/// downloads, the Visual Studio Installer's staged packages, and Blender's session folders (1.9 GB
/// between them on the machine that prompted this).
///
/// <para><b>Tier 2, because what goes is downloaded again, not rebuilt.</b> An updater that finds its
/// download gone fetches it again, and the Visual Studio Installer reuses the folder it stages
/// packages in for the next update — 1.39 GB of Windows SDK installers were kept there for exactly
/// that. Nothing is lost, but the next update costs the download. A Blender session folder is
/// dearer in a different way: a bake made for a file that was never saved points into the session's
/// folder, so a recovered autosave can reach for it.</para>
///
/// <para><b>Held back while its application runs</b>, because none of these names ties a folder to a
/// process. VS Code applies a downloaded update from its folder when it restarts, and deleting the
/// flag file beside the installer changes what the installer does; Docker's update behaviour is not
/// published, so it is treated the same way. The clean asks again before removing each one, so an
/// application started while the preview was on screen keeps its download.</para>
///
/// <para><b>Blender's unsaved work is named here so that nothing takes it.</b> <c>quit.blend</c> and
/// the autosave files sit loose in the temporary folder, where the "Temporary files" row would take
/// them on their age. They hold work that was never saved anywhere else, so this row recognises them,
/// keeps them, and asserts they survived.</para>
/// </summary>
public sealed partial class TempInstallerDownloadProvider : TempMarkerProviderBase
{
    private const string VsCodeTool = "VS Code updater";
    private const string DockerTool = "Docker Desktop updater";
    private const string VisualStudioTool = "Visual Studio Installer";
    private const string BlenderTool = "Blender";

    /// <summary>
    /// Where each Visual Studio instance keeps this account's state, including the folder its
    /// installer stages packages in. The random name of that folder is not recognisable any other way.
    /// </summary>
    private static readonly string[] InstancesPath = ["Microsoft", "VisualStudio", "Packages", "_Instances"];

    /// <summary>An instance's state file is a few hundred bytes. Anything far larger is not it.</summary>
    private const int MaximumStateBytes = 1024 * 1024;

    /// <summary>
    /// The folder VS Code's updater downloads into: its quality, whether it is a user or system
    /// install, and the architecture. The <c>vscode-update-</c> form is what releases up to 1.75 used.
    ///
    /// <para>Nothing else of VS Code's in the temporary folder matches, and that is the point:
    /// <c>vscode-typescript</c> is the TypeScript server's live working folder.</para>
    /// </summary>
    [GeneratedRegex(
        @"\Avscode-(?:stable|insider|update)-(?:user|system)-(?:x64|arm64|ia32)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VsCodeUpdate();

    [GeneratedRegex(@"\ADockerDesktopUpdates\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DockerUpdates();

    /// <summary>A package the Visual Studio Installer staged: its identifier, then twenty hexadecimal digits.</summary>
    [GeneratedRegex(@"\A[0-9a-z_.\-]+\.[0-9a-f]{20}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StagedPackage();

    /// <summary>
    /// A Blender session folder: the template <c>blender_XXXXXX</c> as the C runtime's
    /// <c>_mktemp_s</c> fills it — one letter, then five digits of a thread identifier.
    /// </summary>
    [GeneratedRegex(@"\Ablender_[a-z][0-9]{5}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlenderSession();

    [GeneratedRegex(@"\Aquit\.blend\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlenderQuit();

    /// <summary>An autosave: the process identifier, with the file's own name before it where it had one.</summary>
    [GeneratedRegex(@"\A(?:.+_)?[0-9]+_autosave\.blend\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlenderAutosave();

    private static readonly TempMarker[] TopLevel =
    [
        new(
            VsCodeTool,
            VsCodeUpdate(),
            TargetKind.Directory,
            "An update VS Code downloaded to install itself. VS Code downloads it again if it still "
            + "needs it.")
        {
            HeldBy = ["Code", "Code - Insiders", "inno_updater"],
        },
        new(
            DockerTool,
            DockerUpdates(),
            TargetKind.Directory,
            "An update Docker Desktop downloaded to install itself. Docker Desktop downloads it again "
            + "if it still needs it.")
        {
            HeldBy = ["Docker Desktop", "com.docker.backend"],
        },
        new(
            BlenderTool,
            BlenderSession(),
            TargetKind.Directory,
            "A working folder Blender made for one session and would have removed on exit. It was "
            + "left because Blender was killed. A bake made for a file that was never saved points in "
            + "here, so recovering that file will not find it.")
        {
            HeldBy = ["blender"],
        },
        new(
            BlenderTool,
            BlenderQuit(),
            TargetKind.File,
            "Blender's copy of the last session, which File › Recover › Last Session opens. It may be "
            + "the only copy of unsaved work, so it is never removed.")
        {
            Keeps = true,
        },
        new(
            BlenderTool,
            BlenderAutosave(),
            TargetKind.File,
            "A file Blender autosaved, which File › Recover › Auto Save opens. It may be the only copy "
            + "of unsaved work, so it is never removed.")
        {
            Keeps = true,
        },
    ];

    private static readonly TempMarker Staged = new(
        VisualStudioTool,
        StagedPackage(),
        TargetKind.Directory,
        "A package the Visual Studio Installer downloaded and keeps for the next install or update. "
        + "It downloads the package again when it needs it.")
    {
        HeldBy = ["setup", "vs_installer", "vs_installershell", "BackgroundDownload"],
    };

    public TempInstallerDownloadProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null)
        : base(environment, runner, inspector, scanner, system, liveTrees)
    {
    }

    public override string Id => "temp-installer-downloads";

    public override string Name => "Installer downloads in temporary folders";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next update of VS Code, Docker Desktop or Visual Studio downloads what it needs again, "
        + "which for Visual Studio can be more than a gigabyte. Nothing is removed while its "
        + "application is running, and Blender's recovery files are never removed.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "VS Code, Docker Desktop, the Visual Studio Installer and Blender",
        Publisher = "Microsoft, Docker and the Blender Foundation",
        Purpose = "These applications download their updates into your temporary folder and keep "
            + "them there, and Blender makes a working folder there for each session that is left "
            + "behind whenever Blender is closed forcibly.",
        Recommendation = "Deguffer offers only folders these applications' own names identify, and "
            + "only while the application is not running. An update downloads again when it is next "
            + "needed. Blender's recovery files are kept.",
    };

    protected override string NothingLeftBehind =>
        "None of these applications has left a download or a working folder in a temporary folder.";

    protected override IReadOnlyList<TempMarkerPlace> PlacesIn(IReadOnlyList<string> accountFolders)
    {
        var places = accountFolders.Select(folder => new TempMarkerPlace(folder, TopLevel)).ToList();

        foreach (var staging in VisualStudioStaging())
        {
            var parent = Path.GetDirectoryName(LongPath.Unaliased(staging));

            // Recognised only directly inside one of this account's temporary folders, where the
            // installer puts it. A state file naming somewhere else is not followed: this row is about
            // the temporary folder, and a path read from a file is a path somebody else chose.
            if (accountFolders.FirstOrDefault(folder =>
                    LongPath.Unaliased(Path.TrimEndingDirectorySeparator(folder)).Equals(parent, StringComparison.OrdinalIgnoreCase))
                is { } folder)
            {
                places.Add(new TempMarkerPlace(
                    Path.Combine(folder, Path.GetFileName(staging)), [Staged], VisualStudioTool, folder));
            }
        }

        return places;
    }

    /// <summary>
    /// The folder each Visual Studio instance stages packages in, as its state file names it.
    /// </summary>
    private IEnumerable<string> VisualStudioStaging()
    {
        var instances = Path.Combine([Environment.LocalAppData, .. InstancesPath]);

        foreach (var instance in ChildDirectories.Under(instances).Directories)
        {
            using var state = BoundedJsonFile.Read(Path.Combine(instance.FullName, "state.json"), MaximumStateBytes);

            if (state is not null
                && LongPath.Configured(BoundedJsonFile.StringProperty(state.RootElement, "temporaryCache")) is { } staging)
            {
                yield return staging;
            }
        }
    }
}
