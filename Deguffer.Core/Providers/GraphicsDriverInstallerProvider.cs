using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What NVIDIA's and AMD's driver installers unpack or download and leave behind: the payload of a
/// driver that is already installed, several hundred megabytes a release.
///
/// <para><b>Not measured.</b> No machine with any of these folders was available when this was
/// written, so every rule here comes from the installers themselves and from what their vendors and
/// users report. <c>docs/cache-locations.md</c> lists the sources. Where they do not settle a name,
/// the name is not recognised, which costs an incomplete reclaim rather than a guess.</para>
///
/// <para><b>Tier 2, where the survey that proposed this said Tier 1.</b> §3's Tier 1 is content its
/// producer re-creates on demand. Nothing re-creates an installer payload: the installed driver runs
/// from the driver store and never reads these, and the only thing that needs one again is
/// reinstalling that release, which means downloading it again. That is Tier 2's consequence exactly,
/// and it keeps a location nobody has measured out of the default selection.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> None of the three vendors ships a command that removes
/// its downloaded packages. AMD's installer clears its older payloads from <c>C:\AMD</c> itself from
/// Adrenalin 24.1.1 on, to save space, so AMD's own installer treats them as disposable, and
/// NVIDIA's installers from 576.80 on unpack into a temporary folder and remove it. What they leave
/// from before then is what this reaches.</para>
///
/// <para><b>§5.2 is the whole of the safety argument, and it is stricter here than for a tool's
/// cache.</b> Two of the three folders sit at the top of the system drive, so the drive is never
/// listed and every folder on the way down is reached by name: see
/// <see cref="InstallerPayloadRoot"/>. Only the payload folder itself is listed, and in each one the
/// siblings that matter are named and asserted to have survived:</para>
/// <list type="bullet">
/// <item><c>C:\AMD\Chipset_Software</c> is the install source of the chipset driver. Windows
/// Installer reads it to repair, upgrade or remove that driver, and a reinstall fails with error
/// 1308 once it has gone.</item>
/// <item><c>C:\AMD\WU-CCC2</c> is what a driver Windows Update installed is removed through.</item>
/// <item><c>C:\ProgramData\NVIDIA Corporation</c> holds the NVIDIA app's own update store beside the
/// old downloader. Somebody who cleared that store reported games crashing afterwards, so it is not
/// reached at all.</item>
/// </list>
///
/// <para><b>Presence is a payload with something in it, never a folder existing.</b> An installer
/// that tidies up after itself removes the files and can leave the folders that held them, and an
/// AMD machine keeps <c>C:\AMD</c> for its chipset driver alone. A row for either would tell the user
/// there is something to reclaim when there is not.</para>
///
/// <para><b>No administrator rights, measured for the parents and inferred for the folders
/// themselves.</b> The top of the system drive was measured granting Authenticated Users inherited
/// modify rights on every folder created below it, and another vendor's folder there was measured
/// carrying them. <c>%PROGRAMDATA%\NVIDIA Corporation</c> was measured granting Everyone full
/// control. None of the three payload folders was on the machine, so an installer that sets rights
/// of its own on one is not ruled out. If one does, the removal is refused and reported, which is the
/// direction <see cref="EpicLauncherContentCacheProvider"/> explains is the right one to be wrong
/// in.</para>
/// </summary>
public sealed partial class GraphicsDriverInstallerProvider : InstallerPayloadProvider
{
    public GraphicsDriverInstallerProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            Candidates(system ?? SystemDirectories.Current))
    {
    }

    public override string Id => "graphics-driver-installers";

    public override string Name => "Graphics driver installer files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "Nothing changes for the driver you are running, which Windows keeps in its own store. To "
        + "reinstall one of these releases, or to repair an older AMD Software installation that "
        + "asks for its setup files, download the installer again from the vendor.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "the NVIDIA and AMD graphics driver installers, and GeForce Experience",
        Publisher = "NVIDIA and AMD",
        Purpose = "A driver installer unpacks the whole driver package before installing it, and "
            + "older releases left that copy behind, one folder per release. GeForce Experience "
            + "also kept the driver packages it downloaded. Each one is several hundred megabytes.",
        Recommendation = "The driver you are running does not use these. AMD's own installer "
            + "clears its older copies itself, and the only cost of removing them is downloading a "
            + "release again should you want to reinstall it.",
    };

    protected override string Payloads => "driver packages";

    protected override string NothingFound =>
        "No graphics driver installer has left its files in the places Deguffer knows about.";

    /// <summary>
    /// §5.3: AMD's installer unpacks into <c>AMD-Software-Installer</c> and removes it when it
    /// finishes, so while it runs that folder is the installation in progress.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["AMDSoftwareInstaller"];

    private static IEnumerable<InstallerPayloadRoot> Candidates(ISystemDirectories system) =>
    [
        new InstallerPayloadRoot(
            @"NVIDIA\DisplayDriver",
            "NVIDIA",
            system.SystemDrive,
            Path.Combine("NVIDIA", "DisplayDriver"),
            ClassifyNvidiaRelease,
            []),

        new InstallerPayloadRoot(
            @"NVIDIA Corporation\Downloader",
            "NVIDIA",
            system.ProgramData,
            Path.Combine("NVIDIA Corporation", "Downloader"),
            ClassifyNvidiaDownload,
            [
                ("config", "GeForce Experience's settings for its downloader. It is not a package."),
                ("status.json", "GeForce Experience's record of what it has downloaded. It is not a package."),
            ]),

        new InstallerPayloadRoot(
            "AMD",
            "AMD",
            system.SystemDrive,
            "AMD",
            ClassifyAmdPayload,
            [.. AmdKeptChildren.Select(c => (c.Key, c.Value))]),
    ];

    /// <summary>
    /// NVIDIA's manual installer unpacks into <c>DisplayDriver\&lt;release&gt;</c>, and a release is
    /// always three digits, a point and two digits. Anything else in that folder is not something the
    /// installer wrote there.
    /// </summary>
    private static ChildClassification ClassifyNvidiaRelease(string root, string name) =>
        NvidiaRelease().IsMatch(name)
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                $"NVIDIA driver {name}, unpacked by its installer and left behind after it installed.")
            : Unrecognised(name, "not a driver release NVIDIA's installer unpacks here.");

    /// <summary>
    /// GeForce Experience kept each driver it downloaded in a folder named by a long hexadecimal
    /// identifier, and the newest in <c>latest</c>. Its settings and its record of what it downloaded
    /// sit beside them, and are named on the root rather than recognised here.
    /// </summary>
    private static ChildClassification ClassifyNvidiaDownload(string root, string name) =>
        name.Equals("latest", StringComparison.OrdinalIgnoreCase) || DownloadIdentifier().IsMatch(name)
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "A driver package GeForce Experience downloaded. It downloads a package again when it "
                + "needs one.")
            : Unrecognised(name, "not a driver package GeForce Experience downloads here.");

    /// <summary>
    /// AMD's installer has unpacked into <c>AMD-Software-Installer</c> since 23.7.1. Before that each
    /// release had a folder of its own, under names that were never consistent, so those are
    /// recognised by holding the display driver package rather than by name. The chipset driver's
    /// folders are refused first, whatever they hold, because Windows Installer needs them.
    /// </summary>
    private static ChildClassification ClassifyAmdPayload(string root, string name)
    {
        if (AmdKeptChildren.TryGetValue(name, out var kept))
        {
            return new ChildClassification(name, SafetyTier.DoNotTouch, kept);
        }

        if (name.Equals("AMD-Software-Installer", StringComparison.OrdinalIgnoreCase))
        {
            return new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "Where AMD's installer unpacks itself. It removes this when an installation finishes, "
                + "so one still here is from an installation that stopped part-way.");
        }

        // Asked in two states: a refusal is not evidence that this folder is a driver package. Walked
        // segment by segment as well, because a probe resolves through a link on the way down, and a
        // package found through one says nothing about what the folder itself holds.
        var folder = Path.Combine(root, name);
        var marker = Path.Combine(folder, "Packages", "Drivers", "Display");

        return DerivedPath.FirstObstacleBetween(folder, marker) is null
            && LongPath.ProbeDirectory(marker) is PathPresence.Present
            ? new ChildClassification(
                name,
                SafetyTier.RegenerableWithCost,
                "An older AMD Software release, unpacked by its installer and left behind. AMD's own "
                + "installer clears these itself from Adrenalin 24.1.1 on.")
            : Unrecognised(name, "not a driver package AMD's installer unpacks here.");
    }

    /// <summary>
    /// What AMD keeps in <c>C:\AMD</c> that is not a payload, and what each one is. Named on the root as
    /// well as refused, so §5.6 asserts each survived.
    /// </summary>
    private static readonly Dictionary<string, string> AmdKeptChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Chipset_Software"] = "The chipset driver's install source. Windows Installer reads it to "
            + "repair, upgrade or remove that driver, and cannot once it has gone.",
        ["Chipset_Driver_Installer"] = "An older chipset driver's install source. Whether anything "
            + "still reads it is not established, so it stays.",
        ["Chipset_SoftwareLogs"] = "The chipset driver installer's logs.",
        ["WU-CCC2"] = "What a driver Windows Update installed is removed through.",
    };

    private static ChildClassification Unrecognised(string name, string why) =>
        new(name, SafetyTier.DoNotTouch, $"It is {why}");

    [GeneratedRegex(@"^\d{3}\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaRelease();

    [GeneratedRegex("^[0-9a-f]{16,}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DownloadIdentifier();
}
