using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// <c>C:\Autodesk</c> sits at the top of the system drive, and Autodesk keeps a licence server and
/// administrators keep their deployment images beside the payloads its installers leave there. What
/// has to hold is that the drive is never listed, that only the recognised payloads go, and that
/// those neighbours are still there after a run.
/// </summary>
public sealed class AutodeskInstallerProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public AutodeskInstallerProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Drive => _system.SystemDrive;

    private string Autodesk => Path.Combine(Drive, "Autodesk");

    private AutodeskInstallerProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>A directory with a file at <paramref name="file"/> below it, so it measures above zero.</summary>
    private static string Populate(string directory, string file = "Setup.exe", int bytes = 4096)
    {
        var path = Path.Combine(directory, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return directory;
    }

    /// <summary>A product extracted by a browser download, laid out the way its installer unpacks it.</summary>
    private string Extracted(string name) =>
        Populate(Path.Combine(Autodesk, name), Path.Combine("x64", "acad", "AcadSetup.msi"));

    private string LicenceServer() =>
        Populate(Path.Combine(Autodesk, "Network License Manager"), "lmgrd.exe");

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithoutTheFolder()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// A licence server keeps <c>C:\Autodesk</c> on a machine that has no payload in it, and an
    /// extraction that was tidied up leaves its folder empty. Neither is anything to reclaim.
    /// </summary>
    [Fact]
    public async Task FoldersThatExistWithNothingToReclaimAreNotPresence()
    {
        LicenceServer();
        Directory.CreateDirectory(Path.Combine(Autodesk, "AutoCAD_2024_English_Win_64bit_dlm", "x64"));
        Directory.CreateDirectory(Path.Combine(Autodesk, "WI"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task PlansOneItemPerRecognisedPayload()
    {
        var product = Extracted("AutoCAD_2024_English_Win_64bit_dlm");
        var revision = Extracted("Revit_2020_G1_Win_64bit_r1_dlm");
        var update = Extracted("Autodesk_Maya_2023_2_Update_Windows_64bit_dlm");
        var webInstaller = Populate(Path.Combine(Autodesk, "WI", "ACD_2026_english_us_win_db_002_002"));
        var odis = Populate(Path.Combine(Autodesk, "IM"), Path.Combine("packages", "payload.7z"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { product, revision, update, Path.Combine(Autodesk, "WI"), odis }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.False(plan.RequiresElevation);

        var identities = plan.Steps.OfType<DeleteStep>().Select(s => s.Identity?.Key).ToList();
        Assert.Equal(plan.Steps.Count, identities.Count);
        Assert.All(identities, Assert.NotNull);
        Assert.Equal(identities.Count, identities.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(@"Autodesk\WI", identities);
        Assert.True(Directory.Exists(webInstaller));
    }

    /// <summary>
    /// §5.2 and §5.6, proved by running a plan. The folder itself, the licence server, the deployment
    /// images, Autodesk Access's updates and every unrecognised child are still there, and the top of
    /// the drive was never a target.
    /// </summary>
    [Fact]
    public async Task EverythingButThePayloadsSurvivesARun()
    {
        var product = Extracted("AutoCAD_2024_English_Win_64bit_dlm");
        var webInstaller = Populate(Path.Combine(Autodesk, "WI"), Path.Combine("_Setup_exe", "Setup.exe"));

        var licenceServer = LicenceServer();
        var deployments = Populate(
            Path.Combine(Autodesk, "Deployments", "Revit2022.0.1"), Path.Combine("image", "Collection.xml"));
        var access = Populate(Path.Combine(Autodesk, "Access"), Path.Combine("updates", "update.exe"));

        // Unrecognised: a name no rule matches, a product code Autodesk's installers are not known to
        // write at the top of the folder, and the bare suffix.
        var unrecognised = Populate(Path.Combine(Autodesk, "My Drawings"), "plan.dwg");
        var identifier = Populate(Path.Combine(Autodesk, "{5783F2D7-D001-0000-0102-0060B0CE6BBA}"), Path.Combine("image", "Installer.exe"));
        var suffixOnly = Populate(Path.Combine(Autodesk, "_dlm"), "notes.txt");

        // Beside the folder at the top of the drive, which is never listed.
        var neighbour = Populate(Path.Combine(Drive, "Games"), "save.dat");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { product, webInstaller }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        string[] asserted =
        [
            Drive, Autodesk, licenceServer, Path.Combine(Autodesk, "Deployments"), access,
            unrecognised, identifier, suffixOnly,
        ];

        foreach (var path in asserted)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(product));
        Assert.False(Directory.Exists(webInstaller));

        Assert.All(
            [.. asserted, deployments, neighbour],
            path => Assert.True(Directory.Exists(path), $"{path} was destroyed"));
        Assert.True(File.Exists(Path.Combine(licenceServer, "lmgrd.exe")), "the licence server was destroyed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// What Autodesk keeps here for something else is refused by name, in any case Windows accepts,
    /// whatever it holds. Removing the licence server stops every seat it serves, and a deployment
    /// image is an administrator's own work.
    /// </summary>
    [Theory]
    [InlineData("Network License Manager")]
    [InlineData("network license manager")]
    [InlineData("Deployments")]
    [InlineData("Access")]
    public async Task WhatAutodeskKeepsHereIsRefusedByName(string name)
    {
        var kept = Populate(Path.Combine(Autodesk, name), "lmgrd.exe");
        var product = Extracted("AutoCAD_2024_English_Win_64bit_dlm");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([product], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase));

        // The user is told what it is, not that it is unknown.
        var note = Assert.Single(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain("not something", note.Message, StringComparison.Ordinal);
    }

    /// <summary>A browser download's folder ends in <c>_dlm</c>, and a name that only resembles one is Tier 4.</summary>
    [Theory]
    [InlineData("_dlm")]
    [InlineData("AutoCAD_2024_English_Win_64bit_dlm.old")]
    [InlineData("AutoCAD_2024_English_Win_64bit_dlm_001_002")]
    [InlineData("AutoCAD_2024_English_Win_64bitdlm")]
    [InlineData("WI2")]
    public async Task ANameThatIsNotAnAutodeskPayloadIsLeftAlone(string name)
    {
        var folder = Populate(Path.Combine(Autodesk, name));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A junctioned <c>C:\Autodesk</c> hands the listing the far side's folders. The walk names it
    /// and goes no further.
    /// </summary>
    [Fact]
    public async Task AJunctionedFolderIsNeverLookedThrough()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var bystander = Populate(Path.Combine(outside, "AutoCAD_2024_English_Win_64bit_dlm"), "irreplaceable.bin");

        SymbolicLink.ToDirectory(Autodesk, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Autodesk", StringComparison.Ordinal) && n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(bystander, "irreplaceable.bin")), "planning looked through a junctioned folder");
    }

    /// <summary>A payload that is a link is left alone, and its far side survives.</summary>
    [Fact]
    public async Task APayloadThatIsALinkIsNotDeletedThrough()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"), "irreplaceable.bin");
        Directory.CreateDirectory(Autodesk);
        var link = Path.Combine(Autodesk, "AutoCAD_2024_English_Win_64bit_dlm");
        SymbolicLink.ToDirectory(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a payload was deleted through a link");
    }

    /// <summary>A folder Windows will not list may hold anything, so the row says so rather than calling it empty.</summary>
    [Fact]
    public async Task AFolderThatWillNotBeListedIsReportedRatherThanCalledEmpty()
    {
        Extracted("AutoCAD_2024_English_Win_64bit_dlm");

        using var denied = new DeniedDirectory(Autodesk);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
    }

    /// <summary>§5.3: while Autodesk's installer runs, the folder it extracts into is the installation in progress.</summary>
    [Theory]
    [InlineData("Installer")]
    [InlineData("AdODIS-installer")]
    public async Task WarnsWhileAutodesksInstallerIsRunning(string process)
    {
        Extracted("AutoCAD_2024_English_Win_64bit_dlm");

        var plan = await CreateProvider(new FakeProcessInspector(process)).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>A system drive Windows cannot locate reaches nothing, rather than a folder below the working directory.</summary>
    [Fact]
    public void ADriveWindowsCannotLocateReachesNothing()
    {
        var provider = new AutodeskInstallerProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: new UnlocatedSystemDrive(_system));

        Assert.Empty(provider.ToolRoots);
    }

    private sealed class UnlocatedSystemDrive(FakeSystemDirectories located) : ISystemDirectories
    {
        public string WindowsDirectory => located.WindowsDirectory;

        public string ProgramData => located.ProgramData;

        public string ProgramFiles => located.ProgramFiles;

        public string ProgramFilesX86 => located.ProgramFilesX86;

        public string SystemDrive => string.Empty;
    }

    /// <summary>§7.1: Explore refuses what a plan protects, because it is told the same rule.</summary>
    [Fact]
    public void ExploreIsToldTheSameRuleThePlanUses()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Autodesk, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(root.Recognises("AutoCAD_2024_English_Win_64bit_dlm"));
        Assert.True(root.Recognises("WI"));
        Assert.True(root.Recognises("IM"));
        Assert.False(root.Recognises("Network License Manager"));
        Assert.False(root.Recognises("Deployments"));
        Assert.False(root.Recognises("Access"));
        Assert.False(root.Recognises("My Drawings"));
    }
}
