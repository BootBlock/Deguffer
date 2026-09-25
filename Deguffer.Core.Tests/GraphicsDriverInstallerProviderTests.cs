using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The provider that reaches into the top of the system drive. What has to hold is that it never
/// lists the drive, that each vendor folder gives up only the payloads it recognises, and that the
/// siblings a vendor keeps for something else — the chipset driver's install source above all — are
/// still there after a run.
/// </summary>
public sealed class GraphicsDriverInstallerProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public GraphicsDriverInstallerProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Drive => _system.SystemDrive;

    private string DisplayDriver => Path.Combine(Drive, "NVIDIA", "DisplayDriver");

    private string Downloader => Path.Combine(_system.ProgramData, "NVIDIA Corporation", "Downloader");

    private string Amd => Path.Combine(Drive, "AMD");

    private GraphicsDriverInstallerProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A directory with a file at <paramref name="file"/> below it, so it measures above zero.</summary>
    private static string Populate(string directory, string file = "setup.exe", int bytes = 4096)
    {
        var path = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return directory;
    }

    /// <summary>An NVIDIA release laid out the way its installer unpacks it.</summary>
    private string NvidiaRelease(string version) =>
        Populate(Path.Combine(DisplayDriver, version), Path.Combine("Win11_Win10-DCH_64", "International", "setup.exe"));

    /// <summary>An AMD folder holding the display driver package, which is what marks an older release.</summary>
    private string AmdRelease(string name) =>
        Populate(Path.Combine(Amd, name), Path.Combine("Packages", "Drivers", "Display", "WT6A_INF", "driver.inf"));

    [Fact]
    public async Task ReportsNotPresentOnAMachineHoldingNoneOfThem()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The case the proposal named. An AMD machine keeps <c>C:\AMD</c> for its chipset driver, and an
    /// NVIDIA installer that tidied up leaves the release folders it emptied. Neither is anything to
    /// reclaim, so neither may be a row.
    /// </summary>
    [Fact]
    public async Task FoldersThatExistWithNothingToReclaimAreNotPresence()
    {
        Populate(Path.Combine(Amd, "Chipset_Software"), Path.Combine("Packages", "IODriver", "dpinst64.exe"));
        Directory.CreateDirectory(Path.Combine(DisplayDriver, "546.33", "Win11_Win10-DCH_64"));
        Directory.CreateDirectory(Path.Combine(Downloader, "latest"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task PlansOneItemPerRecognisedPayloadInEachFolder()
    {
        var release = NvidiaRelease("546.33");
        var older = NvidiaRelease("537.58");
        var latest = Populate(Path.Combine(Downloader, "latest"));
        var download = Populate(Path.Combine(Downloader, "3f0c2a9d8b7e4c1a9d2e6f5a4b3c2d1e"));
        var installer = Populate(Path.Combine(Amd, "AMD-Software-Installer"), Path.Combine("Bin64", "AMDSoftwareInstaller.exe.tmp"));
        var legacy = AmdRelease("AMD_Software_Installer_22.10.3");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { release, older, latest, download, installer, legacy }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.False(plan.RequiresElevation);

        // Each is an item the user can keep, so each needs an identity, and two vendors' children of
        // the same name must not share one.
        var identities = plan.Steps.OfType<DeleteStep>().Select(s => s.Identity?.Key).ToList();
        Assert.Equal(plan.Steps.Count, identities.Count);
        Assert.All(identities, Assert.NotNull);
        Assert.Equal(identities.Count, identities.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// §5.2 and §5.6, proved by running a plan. Every folder the provider passes through, every
    /// sibling a vendor keeps for something else and every unrecognised child is still there, and the
    /// top of the drive was never a target.
    /// </summary>
    [Fact]
    public async Task EverythingButThePayloadsSurvivesARun()
    {
        var release = NvidiaRelease("546.33");
        var download = Populate(Path.Combine(Downloader, "latest"));
        var legacy = AmdRelease("AMD_Software_Installer_22.10.3");

        // Kept by the vendor for something else.
        var chipset = Populate(Path.Combine(Amd, "Chipset_Software"), Path.Combine("Packages", "IODriver", "dpinst64.exe"));
        var windowsUpdate = Populate(Path.Combine(Amd, "WU-CCC2"), "ccc2_install.exe");
        var config = Populate(Path.Combine(Downloader, "config"), "config.json");
        var status = Path.Combine(Downloader, "status.json");
        File.WriteAllText(status, "{}");
        var nvidiaApp = Populate(
            Path.Combine(_system.ProgramData, "NVIDIA Corporation", "NVIDIA app", "UpdateFramework", "ota-artifacts", "grd"),
            "package.bin");
        var physX = Populate(Path.Combine(Drive, "NVIDIA", "PhysX", "Temp"), "PhysX.msi");

        // Unrecognised: a name no rule matches, and an AMD folder without the driver package.
        var unrecognisedRelease = Populate(Path.Combine(DisplayDriver, "notes"), "readme.txt");
        var unrecognisedDownload = Populate(Path.Combine(Downloader, "cache"), "index.bin");
        var unrecognisedAmd = Populate(Path.Combine(Amd, "Radeon Recordings"), "clip.mp4");

        // Beside the vendor folders at the top of the drive, which is never listed.
        var neighbour = Populate(Path.Combine(Drive, "Games"), "save.dat");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { release, download, legacy }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        string[] asserted =
        [
            Drive, Path.Combine(Drive, "NVIDIA"), DisplayDriver, _system.ProgramData,
            Path.Combine(_system.ProgramData, "NVIDIA Corporation"), Downloader, Amd,
            chipset, windowsUpdate, config, status, unrecognisedRelease, unrecognisedDownload, unrecognisedAmd,
        ];

        foreach (var path in asserted)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(release));
        Assert.False(Directory.Exists(download));
        Assert.False(Directory.Exists(legacy));

        string[] directories = [.. asserted.Where(p => p != status), nvidiaApp, physX, neighbour];
        Assert.All(directories, path => Assert.True(Directory.Exists(path), $"{path} was destroyed"));
        Assert.True(File.Exists(status), "the downloader's record was destroyed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The chipset driver's install source is refused by name, whatever it holds. A rule that looked
    /// only for the display driver package would take it the day AMD ships one inside it, and
    /// Windows Installer then cannot repair, upgrade or remove the chipset driver.
    /// </summary>
    [Fact]
    public async Task TheChipsetInstallSourceIsRefusedEvenWhenItLooksLikeADriverPackage()
    {
        var chipset = AmdRelease("Chipset_Software");
        var legacy = AmdRelease("AMD_Software_Installer_22.10.3");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([legacy], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Chipset_Software", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(chipset, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// NVIDIA names a release folder three digits, a point and two digits, and nothing else in that
    /// folder is its installer's. A near miss is Tier 4.
    /// </summary>
    [Theory]
    [InlineData("546.3")]
    [InlineData("5466.33")]
    [InlineData("546.33-old")]
    [InlineData("546_33")]
    public async Task ANameThatIsNotAnNvidiaReleaseIsLeftAlone(string name)
    {
        var folder = Populate(Path.Combine(DisplayDriver, name));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same for GeForce Experience's downloads: <c>latest</c>, or a long hexadecimal identifier,
    /// and nothing that only resembles one.
    /// </summary>
    [Theory]
    [InlineData("latest-old")]
    [InlineData("3f0c2a9d8b7e4c1")]
    [InlineData("3f0c2a9d8b7e4c1a9d2e6f5a4b3c2d1g")]
    [InlineData("3f0c2a9d-8b7e-4c1a-9d2e-6f5a4b3c2d1e")]
    public async Task ANameThatIsNotAGeForceExperienceDownloadIsLeftAlone(string name)
    {
        var folder = Populate(Path.Combine(Downloader, name));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A folder Windows cannot locate comes back as an empty string, and a folder named below it would
    /// resolve against Deguffer's own working directory. Nothing is reached there, rather than a guess.
    /// </summary>
    [Fact]
    public void AFolderWindowsCannotLocateReachesNothing()
    {
        var provider = new GraphicsDriverInstallerProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: new UnlocatedSystemDrive(_system));

        var root = Assert.Single(provider.ToolRoots);

        Assert.Equal(Downloader, root.Path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Everything as <see cref="FakeSystemDirectories"/> has it, except a drive Windows would not name.</summary>
    private sealed class UnlocatedSystemDrive(FakeSystemDirectories located) : ISystemDirectories
    {
        public string WindowsDirectory => located.WindowsDirectory;

        public string ProgramData => located.ProgramData;

        public string ProgramFiles => located.ProgramFiles;

        public string ProgramFilesX86 => located.ProgramFilesX86;

        public string SystemDrive => string.Empty;
    }

    /// <summary>
    /// A junctioned <c>C:\NVIDIA</c> hands the listing the far side's folders, and a check on
    /// <c>DisplayDriver</c> alone would never see it. The walk down names it and goes no further.
    /// </summary>
    [Fact]
    public async Task AJunctionOnTheWayDownIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = Populate(Path.Combine(outside, "DisplayDriver", "546.33"), "irreplaceable.bin");

        Directory.CreateSymbolicLink(Path.Combine(Drive, "NVIDIA"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("NVIDIA", StringComparison.Ordinal) && n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(bystander, "irreplaceable.bin")), "planning looked through a junctioned parent");
    }

    /// <summary>
    /// A payload folder Windows will not list may hold anything, so the row appears and says why it
    /// cannot say more, rather than reporting the vendor's folder as holding nothing.
    /// </summary>
    [Fact]
    public async Task AFolderThatWillNotBeListedIsReportedRatherThanCalledEmpty()
    {
        NvidiaRelease("546.33");

        using var denied = new DeniedDirectory(DisplayDriver);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
    }

    /// <summary>§5.3: while AMD's installer runs, the folder it unpacks into is the installation in progress.</summary>
    [Fact]
    public async Task WarnsWhileAmdsInstallerIsRunning()
    {
        Populate(Path.Combine(Amd, "AMD-Software-Installer"), Path.Combine("Bin64", "AMDSoftwareInstaller.exe"));

        var plan = await CreateProvider(new FakeProcessInspector("AMDSoftwareInstaller")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// §7.1: Explore refuses what a plan protects. Each folder is declared with the same rule the plan
    /// uses, so the chipset install source and an unrecognised child are refused there as well.
    /// </summary>
    [Fact]
    public void ExploreIsToldTheSameRuleThePlanUses()
    {
        AmdRelease("AMD_Software_Installer_22.10.3");
        AmdRelease("Chipset_Software");

        var roots = CreateProvider().ToolRoots;

        var nvidia = Assert.Single(roots, r => r.Path.Equals(DisplayDriver, StringComparison.OrdinalIgnoreCase));
        Assert.True(nvidia.Recognises("546.33"));
        Assert.False(nvidia.Recognises("notes"));

        var downloader = Assert.Single(roots, r => r.Path.Equals(Downloader, StringComparison.OrdinalIgnoreCase));
        Assert.True(downloader.Recognises("latest"));
        Assert.False(downloader.Recognises("config"));

        var amd = Assert.Single(roots, r => r.Path.Equals(Amd, StringComparison.OrdinalIgnoreCase));
        Assert.True(amd.Recognises("AMD-Software-Installer"));
        Assert.True(amd.Recognises("AMD_Software_Installer_22.10.3"));
        Assert.False(amd.Recognises("Chipset_Software"));
        Assert.False(amd.Recognises("Radeon Recordings"));

        Assert.DoesNotContain(roots, r => r.Path.Equals(Drive, StringComparison.OrdinalIgnoreCase));
    }
}
