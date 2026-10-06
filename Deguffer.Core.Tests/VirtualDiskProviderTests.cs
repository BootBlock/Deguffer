using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The WSL and Docker virtual disks, found from the tools' own records, measured from outside, and
/// reported with the vendor's route and no step (§5.4, and <c>unreached-locations.md</c> §11).
///
/// <para>Every test reads WSL's registration and Docker Desktop's settings through
/// <see cref="FakeUserEnvironment"/>, and every disk is a file in the test's own folder. Docker is a
/// <see cref="FakeProcessRunner"/> that throws for any program it was not told about, so a test that
/// set up no reply also proves that nothing was run.</para>
/// </summary>
public sealed class VirtualDiskProviderTests : IDisposable
{
    private const string Ubuntu = "{0b6f3c42-1d5e-4f39-9a6c-2f1e0d7b8a11}";
    private const string Debian = "{5c2e9d10-7a4b-4c61-8e3f-6d9a1b2c3e44}";
    private const string DockerData = "{9e8d7c6b-5a49-4382-9171-605f4e3d2c1b}";

    private const int Wsl2 = 0xF;
    private const int Wsl1 = 0x7;
    private const int Installed = 1;

    private const string DockerUsage =
        """
        {"Active":"1","Reclaimable":"1.2GB (60%)","Size":"2GB","TotalCount":"4","Type":"Images"}
        {"Active":"0","Reclaimable":"300MB (100%)","Size":"300MB","TotalCount":"2","Type":"Containers"}
        {"Active":"1","Reclaimable":"500MB (50%)","Size":"1GB","TotalCount":"3","Type":"Local Volumes"}
        {"Active":"0","Reclaimable":"250MB","Size":"250MB","TotalCount":"9","Type":"Build Cache"}
        """;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public VirtualDiskProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private VirtualDiskProvider Provider(FakeProcessRunner? runner = null, FakeProcessInspector? inspector = null) =>
        new(_system, _environment, runner ?? new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    /// <summary>A WSL registration, as WSL writes it, naming <paramref name="basePath"/>.</summary>
    private void Register(
        string key, string name, string basePath, int flags = Wsl2, int state = Installed, string? diskName = null)
    {
        var path = $@"{WslRegistrations.LxssKey}\{key}";

        _environment
            .WithRegistryValue(path, "DistributionName", name)
            .WithRegistryValue(path, "BasePath", basePath)
            .WithRegistryNumber(path, "Flags", flags)
            .WithRegistryNumber(path, "State", state);

        if (diskName is not null)
        {
            _environment.WithRegistryValue(path, "VhdFileName", diskName);
        }
    }

    private string Folder(params string[] parts)
    {
        var folder = Path.Combine([_temp.Path, .. parts]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Disk(string folder, string name = WslRegistrations.DefaultDiskName, int bytes = 4096)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private void DockerSettings(string json)
    {
        var folder = Path.Combine(_environment.RoamingAppData, "Docker");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings-store.json"), json);
    }

    private static string Text(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    [Fact]
    public async Task ARegisteredDistributionsDiskIsFoundFromItsBasePathAndAnUnregisteredOneBesideItIsNot()
    {
        var folder = Folder("wsl", "Ubuntu");
        var disk = Disk(folder);
        var stray = Disk(folder, "copy-of-ext4.vhdx");
        Register(Ubuntu, "Ubuntu", folder);

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(disk, Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain(stray, Text(plan), StringComparison.Ordinal);
        Assert.Contains("The WSL distribution 'Ubuntu'", Text(plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// The row offers nothing and says so, rather than "Already clear", which would be hidden by default
    /// and would claim the opposite of what the report is for.
    /// </summary>
    [Fact]
    public async Task ThePlanOnlyReportsAndTheRowSaysSo()
    {
        Register(Ubuntu, "Ubuntu", Folder("wsl", "Ubuntu"));
        Disk(Path.Combine(_temp.Path, "wsl", "Ubuntu"));

        var provider = Provider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.ReportsOnly);
        Assert.Equal(FindingStatus.ReportOnly, new Finding(provider, true, plan).ToStatus(isElevated: false));
        Assert.Contains(plan.Notes, note => note.Message == VirtualDiskRoutes.InsideIsNotOnTheDrive
            || note.Message.EndsWith(VirtualDiskRoutes.InsideIsNotOnTheDrive, StringComparison.Ordinal));
        Assert.Contains(plan.Notes, note => note.Message == VirtualDiskRoutes.ReportOnly);
    }

    [Fact]
    public async Task AMissingDiskIsReportedAsMissingRatherThanAsZero()
    {
        var folder = Folder("wsl", "Ubuntu");
        Register(Ubuntu, "Ubuntu", folder);

        var provider = Provider();
        var plan = await provider.PlanAsync();
        var missing = Assert.Single(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains("missing", missing.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(folder, WslRegistrations.DefaultDiskName), missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0 B", Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("on this drive. ", Text(plan), StringComparison.Ordinal);
    }

    /// <summary>A disk Windows will not describe is not a disk that is missing.</summary>
    [Fact]
    public async Task ADiskWindowsWillNotDescribeIsNotReportedAsMissing()
    {
        var folder = Folder("wsl", "Ubuntu");
        var disk = Disk(folder);
        Register(Ubuntu, "Ubuntu", folder);

        using var denied = DeniedDirectory.WithUnreadableFile(disk);

        var plan = await Provider().PlanAsync();

        Assert.Contains("would not let Deguffer read", Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("missing", Text(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWsl1DistributionHasNoDiskToReport()
    {
        var folder = Folder("wsl", "Legacy");
        Disk(folder);
        Register(Ubuntu, "Legacy", folder, flags: Wsl1);

        Assert.False(await Provider().IsPresentAsync());
    }

    /// <summary>A distribution WSL is still installing or already removing has no settled disk.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void ADistributionThatIsNotSettledIsLeftOut(int state)
    {
        Register(Ubuntu, "Ubuntu", Folder("wsl", "Ubuntu"), state: state);

        Assert.Empty(VirtualDiskInventory.Read(_environment, _system).Disks);
    }

    [Fact]
    public void OnlyARegistrationNamedByAnIdentifierIsADistribution()
    {
        Register("AppxInstallerCache", "Ubuntu", Folder("wsl", "Ubuntu"));

        Assert.Empty(VirtualDiskInventory.Read(_environment, _system).Disks);
    }

    /// <summary>
    /// WSL has been seen to write the device prefix into <c>BasePath</c>. The disk's path loses it, so
    /// it is the path Explore draws and the note names.
    /// </summary>
    [Fact]
    public void ABasePathWithTheDevicePrefixNamesThePlainPath()
    {
        var folder = Folder("wsl", "Ubuntu");
        Register(Ubuntu, "Ubuntu", @"\\?\" + folder);

        var disk = Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks);

        Assert.Equal(Path.Combine(folder, WslRegistrations.DefaultDiskName), disk.Path);
    }

    [Fact]
    public void TheDiskFileWslNamesIsReadAndANameThatIsAPathIsRefused()
    {
        var imported = Folder("wsl", "Imported");
        Register(Ubuntu, "Imported", imported, diskName: "imported.vhd");
        Register(Debian, "Escaping", Folder("wsl", "Escaping"), diskName: @"..\..\elsewhere.vhdx");

        var disk = Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks);

        Assert.Equal(Path.Combine(imported, "imported.vhd"), disk.Path);
    }

    [Fact]
    public void DockerDesktopsDataDiskIsWhereItsSettingsPutIt()
    {
        var moved = Folder("D", "DockerWSL");
        DockerSettings($$"""{"wslEngineEnabled": true, "CustomWslDistroDir": {{System.Text.Json.JsonSerializer.Serialize(moved)}}}""");

        var disk = Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks);

        Assert.Equal(Path.Combine(moved, "disk", "docker_data.vhdx"), disk.Path);
        Assert.Equal(VirtualDiskKind.DockerData, disk.Kind);
    }

    [Fact]
    public void DockerDesktopsDataDiskIsInItsDocumentedPlaceWhenTheSettingsDoNotMoveIt()
    {
        DockerSettings("""{"autoPauseTimeoutSeconds": 300}""");

        var disk = Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks);

        Assert.Equal(Path.Combine(_environment.LocalAppData, "Docker", "wsl", "disk", "docker_data.vhdx"), disk.Path);
    }

    [Fact]
    public void TheHyperVBackendsDiskIsReadFromItsOwnSetting()
    {
        DockerSettings("""{"WslEngineEnabled": false}""");
        Assert.Equal(
            Path.Combine(_system.ProgramData, "DockerDesktop", "vm-data", "DockerDesktop.vhdx"),
            Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks).Path);

        var moved = Folder("D", "HyperV");
        DockerSettings($$"""{"wslEngineEnabled": false, "dataFolder": {{System.Text.Json.JsonSerializer.Serialize(moved)}}}""");
        Assert.Equal(
            Path.Combine(moved, "DockerDesktop.vhdx"),
            Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks).Path);
    }

    /// <summary>
    /// An installation from before 4.30 keeps its docker-desktop-data distribution, and its settings
    /// name a newer disk that was never made. That one is not reported as missing.
    /// </summary>
    [Fact]
    public void AnOlderDockerInstallationsDiskIsTheRegisteredOne()
    {
        var older = Folder("Docker", "wsl", "data");
        Disk(older);
        Register(DockerData, "docker-desktop-data", older);
        DockerSettings("{}");

        var disk = Assert.Single(VirtualDiskInventory.Read(_environment, _system).Disks);

        Assert.Equal(Path.Combine(older, WslRegistrations.DefaultDiskName), disk.Path);
        Assert.Equal(VirtualDiskKind.DockerData, disk.Kind);
    }

    [Fact]
    public async Task UnreadableDockerSettingsAreSaidRatherThanTakenForNoDocker()
    {
        DockerSettings("{ this is not json");

        var provider = Provider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains("Docker Desktop's settings", Text(await provider.PlanAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADockerEngineThatIsNotRunningGivesTheHostFigureOnlyAndSaysWhy()
    {
        DockerSettings("{}");
        Disk(Folder("profile", "AppData", "Local", "Docker", "wsl", "disk"), "docker_data.vhdx", bytes: 8192);
        _environment.WithExecutable("docker");
        var runner = new FakeProcessRunner();

        var plan = await Provider(runner).PlanAsync();

        Assert.Contains($"{FreeSpace.Format(8192)} on the drive", Text(plan), StringComparison.Ordinal);
        Assert.Contains("Docker Desktop is not running", Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("reclaimable (", Text(plan), StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ARunningEngineGivesDockersOwnFigureBesideTheHostOne()
    {
        DockerSettings("{}");
        Disk(Folder("profile", "AppData", "Local", "Docker", "wsl", "disk"), "docker_data.vhdx");
        _environment.WithExecutable("docker");
        var docker = _environment.FindExecutable("docker")!;
        var runner = new FakeProcessRunner().Responding(docker, "system df", DockerUsage);

        var plan = await Provider(runner, new FakeProcessInspector(DockerInsideFigure.EngineProcess)).PlanAsync();
        var inside = Assert.Single(plan.Notes, note => note.Message.StartsWith("Inside Docker's data disk", StringComparison.Ordinal));

        Assert.Contains($"{FreeSpace.Format(3_550_000_000)} in use", inside.Message, StringComparison.Ordinal);
        Assert.Contains($"{FreeSpace.Format(2_250_000_000)} is reclaimable", inside.Message, StringComparison.Ordinal);
        Assert.Contains("Unused volumes hold data", inside.Message, StringComparison.Ordinal);
        Assert.Contains($"{FreeSpace.Format(4096)} on the drive", Text(plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one command this provider may run, and the only one it runs, across presence, planning, the
    /// declarations Explore reads, a run and its verification: Docker's own read of its disk usage, sent
    /// to Docker Desktop's context. Nothing that prunes, compacts, shuts down or changes a disk.
    /// </summary>
    [Fact]
    public async Task NoCommandThatChangesStateIsEverRun()
    {
        var folder = Folder("wsl", "Ubuntu");
        Disk(folder);
        Register(Ubuntu, "Ubuntu", folder);
        DockerSettings("{}");
        Disk(Folder("profile", "AppData", "Local", "Docker", "wsl", "disk"), "docker_data.vhdx");
        _environment.WithExecutable("docker").WithExecutable("wsl");
        var docker = _environment.FindExecutable("docker")!;
        var runner = new FakeProcessRunner().Responding(docker, "system df", DockerUsage);

        var provider = Provider(runner, new FakeProcessInspector(DockerInsideFigure.EngineProcess));
        await provider.IsPresentAsync();
        await provider.DiscoverToolRootsAsync();
        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);
        await provider.VerifyAsync(plan);

        var call = Assert.Single(runner.Invocations);
        Assert.Equal(docker, call.FileName);
        Assert.Equal("--context desktop-linux system df --format \"{{json .}}\"", call.Arguments);
    }

    [Fact]
    public async Task ASparseDiskIsReportedAsSparseWithItsOwnRoute()
    {
        var folder = Folder("wsl", "Ubuntu");
        var disk = Path.Combine(folder, WslRegistrations.DefaultDiskName);
        SparseFile.Create(disk, 1_000_000_000);
        Register(Ubuntu, "Ubuntu", folder);

        var size = VirtualDiskFile.Measure(disk);
        var plan = await Provider().PlanAsync();

        Assert.True(size.IsSparse);
        Assert.Equal(1_000_000_000, size.Length);
        Assert.True(size.Taken < 1_000_000, $"A sparse file with nothing written took {size.Taken} bytes.");
        Assert.Contains("It is sparse", Text(plan), StringComparison.Ordinal);
        Assert.Contains("fstrim", Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("--compact", Text(plan), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryDiskIsGivenWslsOwnCompactionInOrder()
    {
        var folder = Folder("wsl", "Ubuntu");
        Disk(folder);
        Register(Ubuntu, "Ubuntu", folder);

        var text = Text(await Provider().PlanAsync());

        Assert.False(VirtualDiskFile.Measure(Path.Combine(folder, WslRegistrations.DefaultDiskName)).IsSparse);
        Assert.Contains("wsl --manage Ubuntu --compact", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("delete what is no longer needed", StringComparison.Ordinal)
                < text.IndexOf("--compact", StringComparison.Ordinal),
            "The route names pruning inside before compacting the disk.");
    }

    /// <summary>
    /// Explore refuses the disk and the folder it is in, and nothing else there: a distribution can be
    /// imported to the top of a drive, and what else is there is the user's.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheDiskAndLeavesWhatIsBesideIt()
    {
        var folder = Folder("wsl", "Ubuntu");
        Disk(folder);
        Register(Ubuntu, "Ubuntu", folder);

        var root = Assert.Single(await Provider().DiscoverToolRootsAsync());

        Assert.Equal(folder, root.Path);
        Assert.False(root.Recognises(new ToolRootChild(WslRegistrations.DefaultDiskName, ChildKind.File)));
        Assert.True(root.Recognises(new ToolRootChild("notes.txt", ChildKind.File)));
        Assert.Contains("Ubuntu", root.Reason, StringComparison.Ordinal);
    }

    /// <summary>Hovering a disk in Explore names its owner and its route, from the same text as the report.</summary>
    [Fact]
    public void ExploreSaysWhoseDiskItIsAndHowItIsMadeSmaller()
    {
        var folder = Folder("wsl", "Ubuntu");
        var disk = Disk(folder);
        Register(Ubuntu, "Ubuntu", folder);

        var item = ItemGuide.For(_system, _environment, new FakeVolumeInventory()).Describe(disk);

        Assert.NotNull(item);
        Assert.Contains("WSL distribution 'Ubuntu'", item.Summary, StringComparison.Ordinal);
        Assert.Contains("wsl --manage Ubuntu --compact", item.Removal, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', item.Removal);
    }

    [Fact]
    public async Task NoRecordMeansNoRow()
    {
        Assert.False(await Provider().IsPresentAsync());
        Assert.Empty(VirtualDiskInventory.Read(_environment, _system).Disks);
    }
}
