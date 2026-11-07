using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The tool archives ESP-IDF's installers keep in <c>dist</c>. They are reached by name inside a tools
/// folder every ESP-IDF version shares, so what has to be shown is that only the archive files in
/// <c>dist</c> go, that the installed tools, the Python environments, the installers' records, every
/// ESP-IDF checkout and anything unrecognised survive, that nothing is offered while the Installation
/// Manager runs, and that a folder a setting or a record names is followed only where it may be ESP-IDF's.
/// </summary>
public sealed class EspIdfArchiveProviderTests : IDisposable
{
    private const string Toolchain = "xtensa-esp-elf-14.2.0_20241119-x86_64-w64-mingw32.zip";

    private const string Debugger = "openocd-esp32-win64-0.12.0-esp32-20241016.zip";

    private const string Emulator = "qemu-xtensa-softmmu-esp_develop_9.0.0_20240606-x86_64-w64-mingw32.tar.xz";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    public EspIdfArchiveProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        _volumes.With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultTools => Path.Combine(_environment.UserProfile, ".espressif");

    private string DefaultDist => Path.Combine(DefaultTools, "dist");

    /// <summary>The Installation Manager's folder, <c>C:\Espressif</c> on a real machine.</summary>
    private string Manager => Path.Combine(_system.SystemDrive, "Espressif");

    private string ManagerDist => Path.Combine(Manager, "dist");

    private string ManagerTools => Path.Combine(Manager, "tools");

    private string Record => Path.Combine(ManagerTools, "eim_idf.json");

    private EspIdfArchiveProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system, volumes: _volumes);

    private static string Write(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// One tools folder as <c>idf_tools.py</c> leaves it: archives in every form it unpacks and a download
    /// that did not finish, in <c>dist</c>. Beside them, what must survive: an archive name in a folder, a
    /// file of another kind and a file without an archive's extension in <c>dist</c>, and the installed
    /// tools, the Python environment, the installer's record, the constraints file, a downloaded ESP-IDF and
    /// an ESP-IDF checkout beside <c>dist</c>.
    /// </summary>
    /// <returns>The archives, which go, and everything else, which stays.</returns>
    private static (string[] Archives, string[] Kept) CreateToolsFolder(string tools, int bytes = 4096) =>
    (
        [
            Write(Path.Combine(tools, "dist", Toolchain), bytes),
            Write(Path.Combine(tools, "dist", Debugger), bytes),
            Write(Path.Combine(tools, "dist", Emulator), bytes),
            Write(Path.Combine(tools, "dist", "cmake-3.30.2-windows-x86_64.tar.gz"), bytes),
            Write(Path.Combine(tools, "dist", "ninja-win-1.12.1.tgz"), bytes),
            Write(Path.Combine(tools, "dist", "dfu-util-0.11-win64.tar.bz2"), bytes),
            Write(Path.Combine(tools, "dist", "esp-rom-elfs-20240305.zip.tmp"), bytes),
        ],
        [
            Write(Path.Combine(tools, "dist", "unpacked.zip", "readme.txt")),
            Write(Path.Combine(tools, "dist", "notes.txt")),
            Write(Path.Combine(tools, "dist", "toolchain.tar")),
            Write(Path.Combine(tools, "tools", "xtensa-esp-elf", "esp-14.2.0_20241119", "bin", "xtensa-esp32-elf-gcc.exe")),
            Write(Path.Combine(tools, "python_env", "idf5.4_py3.11_env", "Scripts", "python.exe")),
            Write(Path.Combine(tools, "idf-env.json"), 512),
            Write(Path.Combine(tools, "espidf.constraints.v5.4.txt"), 512),
            Write(Path.Combine(tools, "frameworks", "esp-idf-v5.4", "README.md")),
            Write(Path.Combine(tools, "v5.3", "esp-idf", "README.md")),
        ]);

    private void WriteRecord(params string[] toolsFolders) =>
        Write(Record, JsonSerializer.Serialize(new
        {
            gitPath = @"C:\Users\testuser\.espressif\tools\idf-git\2.44.0\cmd\git.exe",
            idfInstalled = toolsFolders.Select((folder, index) => new
            {
                activationScript = @"C:\Users\testuser\.espressif\tools\activate.ps1",
                id = $"esp-idf-{index}",
                idfToolsPath = folder,
                name = $"v5.{index}",
                path = $@"C:\esp\v5.{index}\esp-idf",
            }),
            idfSelectedId = "esp-idf-0",
        }));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void AssertProtected(CleanupPlan plan, string path) =>
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

    [Fact]
    public async Task ReportsNotPresentWhereEspIdfHasNeverRun()
    {
        Write(Path.Combine(DefaultTools, "idf-env.json"), 512);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §3. An archive is read only while its tool is installed, so the row is Tier 1, and it is researched,
    /// so it is not ticked. Each archive, and the download that did not finish, is a file of its own.
    /// </summary>
    [Fact]
    public async Task PlansEachArchiveInTheDefaultDistAtTier1()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools, 1024);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(archives.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps, step => Assert.IsType<DeleteFileStep>(step));
        Assert.All(plan.Steps, step => Assert.Equal(DefaultDist, step.Group));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal(ProviderBasis.Researched, provider.Basis);
        Assert.Equal(archives.Length * 1024, plan.EstimatedBytes);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6, proved by running a plan. The archives go and <c>dist</c> stays. Everything else in <c>dist</c>,
    /// the tools folder, and every entry beside <c>dist</c> survive, and each is asserted rather than merely
    /// omitted.
    /// </summary>
    [Fact]
    public async Task EverythingButTheArchivesSurvivesARunAndIsAsserted()
    {
        var (archives, kept) = CreateToolsFolder(DefaultTools);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[]
        {
            DefaultTools,
            DefaultDist,
            Path.Combine(DefaultDist, "unpacked.zip"),
            Path.Combine(DefaultDist, "notes.txt"),
            Path.Combine(DefaultDist, "toolchain.tar"),
            Path.Combine(DefaultTools, "tools"),
            Path.Combine(DefaultTools, "python_env"),
            Path.Combine(DefaultTools, "idf-env.json"),
            Path.Combine(DefaultTools, "espidf.constraints.v5.4.txt"),
            Path.Combine(DefaultTools, "frameworks"),
            Path.Combine(DefaultTools, "v5.3"),
        })
        {
            AssertProtected(plan, path);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(archives, path => Assert.False(File.Exists(path), $"{path} was left."));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the archives."));
        Assert.True(Directory.Exists(DefaultDist));
    }

    /// <summary>The survivors are named for what they are, so the reader can tell the shared tools from an unknown file.</summary>
    [Fact]
    public async Task TheSharedToolsAreNamedForWhatTheyAre()
    {
        CreateToolsFolder(DefaultTools);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(
            "every ESP-IDF version",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(DefaultTools, "tools"), StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "left alone",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(DefaultDist, "notes.txt"), StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>IDF_TOOLS_PATH</c> moves the tools folder, and the default is still examined, because a tool
    /// installed before the variable was set downloaded there.
    /// </summary>
    [Fact]
    public async Task FollowsTheFolderIdfToolsPathNamesAndKeepsTheDefault()
    {
        var moved = Path.Combine(_temp.Path, "esp", "tools");
        var (movedArchives, movedKept) = CreateToolsFolder(moved);
        var (defaultArchives, _) = CreateToolsFolder(DefaultTools);
        _environment.WithEnvironmentVariable(EspIdfLayout.ToolsPathVariable, moved);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            movedArchives.Concat(defaultArchives).Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertProtected(plan, moved);
        AssertProtected(plan, Path.Combine(moved, "tools"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(movedKept, path => Assert.True(File.Exists(path), $"{path} went."));
    }

    /// <summary>The variable naming the default folder, however it is spelt, names one folder, examined once.</summary>
    [Fact]
    public async Task TheVariableNamingTheDefaultDoesNotDeclareItTwice()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools);
        _environment.WithEnvironmentVariable(EspIdfLayout.ToolsPathVariable, DefaultTools + @"\");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(archives.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A variable naming the profile itself is declined and said out loud. The <c>dist</c> in it, which
    /// may be anyone's, is never reached, and Explore refuses the entries ESP-IDF would write there.
    /// </summary>
    [Fact]
    public async Task DeclinesAVariableNamingTheProfileAndReachesNothingInIt()
    {
        var mine = Write(Path.Combine(_environment.UserProfile, "dist", Toolchain));
        _environment.WithEnvironmentVariable(EspIdfLayout.ToolsPathVariable, _environment.UserProfile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(EspIdfLayout.ToolsPathVariable, StringComparison.Ordinal));

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(Path.Combine(_environment.UserProfile, "dist")).IsAllowed);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// The Installation Manager downloads into the <c>dist</c> beside its tools, and an <c>idf_tools.py</c>
    /// run in an environment it activated downloads into the <c>dist</c> inside the tools folder it recorded.
    /// Both are planned, its record survives, and the plan says a download folder set elsewhere is not looked for.
    /// </summary>
    [Fact]
    public async Task PlansTheInstallationManagersDistAndEachRecordedToolsFolder()
    {
        var managed = Write(Path.Combine(ManagerDist, Toolchain));
        var recorded = Write(Path.Combine(ManagerTools, "dist", Debugger));
        var installed = Write(Path.Combine(ManagerTools, "xtensa-esp-elf", "esp-14.2.0_20241119", "bin", "xtensa-esp32-elf-gcc.exe"));
        WriteRecord(ManagerTools);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Equal(
            new[] { managed, recorded }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertProtected(plan, Record);
        AssertProtected(plan, Path.Combine(ManagerTools, "xtensa-esp-elf"));
        Assert.Contains(plan.Notes, n => n.Message.Contains("set anywhere else are not found", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(Record));
        Assert.True(File.Exists(installed));
        Assert.False(File.Exists(managed));
        Assert.False(File.Exists(recorded));
    }

    /// <summary>
    /// The Installation Manager's drive choice moves its tools and its downloads together while the record
    /// stays on the system drive, so the <c>dist</c> beside a recorded <c>tools</c> folder is planned too,
    /// and Explore may take the archives in both.
    /// </summary>
    [Fact]
    public async Task PlansTheDistBesideARecordedToolsFolderOnAnotherDrive()
    {
        var other = Path.Combine(_temp.Path, "drive-d", "Espressif");
        var beside = Write(Path.Combine(other, "dist", Toolchain));
        var inside = Write(Path.Combine(other, "tools", "dist", Debugger));
        var installed = Write(Path.Combine(other, "tools", "xtensa-esp-elf", "esp-14.2.0_20241119", "bin", "xtensa-esp32-elf-gcc.exe"));
        WriteRecord(Path.Combine(other, "tools"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { beside, inside }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        AssertProtected(plan, Path.Combine(other, "tools"));

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.True(policy.MayRemove(beside).IsAllowed, policy.MayRemove(beside).Reason);
        Assert.True(policy.MayRemove(inside).IsAllowed, policy.MayRemove(inside).Reason);
        Assert.False(policy.MayRemove(Path.Combine(other, "tools")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(other, "tools", "xtensa-esp-elf")).IsAllowed);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.True(File.Exists(installed));
        Assert.False(File.Exists(beside));
        Assert.False(File.Exists(inside));
    }

    /// <summary>
    /// A recorded <c>tools</c> folder directly on a drive puts the Installation Manager's downloads in the drive
    /// root, which is declined and said. Nothing there is reached, and Explore refuses the dist it would write.
    /// </summary>
    [Fact]
    public async Task DeclinesTheDriveRootBesideARecordedToolsFolder()
    {
        var mine = Write(Path.Combine(_system.SystemDrive, "dist", Toolchain));
        var recorded = Write(Path.Combine(_system.SystemDrive, "tools", "dist", Debugger));
        WriteRecord(Path.Combine(_system.SystemDrive, "tools"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([recorded], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("downloads would be beside", StringComparison.Ordinal));

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(Path.Combine(_system.SystemDrive, "dist")).IsAllowed);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// A tools folder inside another opens the way down to its archives in Explore, and nothing else: the
    /// Installation Manager's record and its installed tools stay refused.
    /// </summary>
    [Fact]
    public async Task ExploreReachesTheArchivesOfARecordedToolsFolderInsideTheManagersFolder()
    {
        Write(Path.Combine(ManagerDist, Toolchain));
        var recorded = Write(Path.Combine(ManagerTools, "dist", Debugger));
        var installed = Write(Path.Combine(ManagerTools, "xtensa-esp-elf", "esp-14.2.0_20241119", "bin", "xtensa-esp32-elf-gcc.exe"));
        WriteRecord(ManagerTools);

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await CreateProvider().DiscoverToolRootsAsync());

        Assert.True(policy.MayRemove(recorded).IsAllowed, policy.MayRemove(recorded).Reason);
        Assert.False(policy.MayRemove(ManagerTools).IsAllowed);
        Assert.False(policy.MayRemove(Record).IsAllowed);
        Assert.False(policy.MayRemove(installed).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(ManagerTools, "dist")).IsAllowed);
    }

    /// <summary>
    /// A variable naming another tools folder's <c>dist</c> makes that <c>dist</c> a tools folder too. An
    /// archive in it is offered once and is not also a survivor, so §5.6 does not report the removal the user
    /// chose as a failure, and what is not an archive still survives.
    /// </summary>
    [Fact]
    public async Task AToolsFolderThatIsAnotherDistDoesNotProtectItsArchives()
    {
        var outer = Write(Path.Combine(DefaultDist, Toolchain));
        var inner = Write(Path.Combine(DefaultDist, "dist", Debugger));
        var notes = Write(Path.Combine(DefaultDist, "notes.txt"));
        _environment.WithEnvironmentVariable(EspIdfLayout.ToolsPathVariable, DefaultDist);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { outer, inner }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(outer, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(File.Exists(outer));
        Assert.False(File.Exists(inner));
        Assert.True(File.Exists(notes));
    }

    /// <summary>
    /// A tools folder Windows will not list leaves the entries beside <c>dist</c> unasserted, so the plan
    /// says so rather than read as fully checked. The archives in <c>dist</c> are still offered.
    /// </summary>
    [Fact]
    public async Task AToolsFolderThatWillNotBeListedIsSaid()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools);

        using (new DeniedDirectory(DefaultTools))
        {
            var plan = await CreateProvider().PlanAsync();

            Assert.Equal(archives.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
            Assert.True(plan.HasUnreadableRoot);
            Assert.Contains(plan.Notes, n => n.Message.Contains(DefaultTools, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A record that is not the record the Installation Manager writes names no folder, and the plan says
    /// the folders it may name were not ruled out. The Installation Manager's own <c>dist</c> is still planned.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRecordIsSaidAndTheManagersDistIsStillPlanned()
    {
        var managed = Write(Path.Combine(ManagerDist, Toolchain));
        Write(Record, "{ \"idfInstalled\": ");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([managed], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read", StringComparison.Ordinal)
            && n.Message.Contains(Record, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A recorded folder that is not a full path, or is somewhere no tool's folder may be, is declined and
    /// said, and nothing in it is reached.
    /// </summary>
    [Fact]
    public async Task DeclinesARecordedFolderThatIsRelativeOrTheProfile()
    {
        var mine = Write(Path.Combine(_environment.UserProfile, "dist", Toolchain));
        WriteRecord(@"esp\tools", _environment.UserProfile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(@"'esp\tools'", StringComparison.Ordinal)
            && n.Message.Contains("not a full path", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(Path.Combine(_environment.UserProfile, "dist")).IsAllowed);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>A recorded folder that is the default, or the Installation Manager's own, is examined once.</summary>
    [Fact]
    public async Task ARecordedFolderAlreadyExaminedIsNotDeclaredTwice()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools);
        WriteRecord(DefaultTools, DefaultTools.ToUpperInvariant());

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(archives.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.3. The Installation Manager downloads and unpacks one archive after another, and Windows lets a file
    /// it is writing be deleted, so while it runs nothing is offered, each <c>dist</c> is named for why, the
    /// row does not read as clear, and Explore refuses it.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedWhileTheInstallationManagerRuns()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools);

        var provider = CreateProvider(new FakeProcessInspector("eim"));
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(
            "Installation Manager is running",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(DefaultDist, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(archives[0]).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(archives, path => Assert.True(File.Exists(path)));
    }

    /// <summary>The clean asks again, so an Installation Manager started while the preview was on screen holds the archives back.</summary>
    [Fact]
    public async Task AnInstallationManagerStartedAfterThePreviewHoldsTheArchivesBack()
    {
        var (archives, _) = CreateToolsFolder(DefaultTools);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        inspector.WithRunning("eim").PassTheReadingLimit();
        var result = await provider.ExecuteAsync(plan);

        Assert.All(archives, path => Assert.True(File.Exists(path), $"{path} was removed under an Installation Manager started after the preview."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7.1. Explore may take from <c>dist</c> only a file with an archive's name. The tools folder
    /// recognises only <c>dist</c>, which opens the way down to the archives.
    /// </summary>
    [Theory]
    [InlineData(Toolchain, true)]
    [InlineData(Emulator, true)]
    [InlineData("cmake-3.30.2-windows-x86_64.tar.gz", true)]
    [InlineData("ninja-win-1.12.1.TGZ", true)]
    [InlineData("dfu-util-0.11-win64.tar.bz2", true)]
    [InlineData("esp-rom-elfs-20240305.zip.tmp", true)]
    [InlineData(".zip", false)]
    [InlineData("toolchain.tar", false)]
    [InlineData("notes.txt", false)]
    [InlineData("download.tmp", false)]
    [InlineData("toolchain.zip.part", false)]
    public async Task ExploreMayTakeOnlyArchiveFilesFromDist(string name, bool recognised)
    {
        CreateToolsFolder(DefaultTools);

        var roots = await CreateProvider().DiscoverToolRootsAsync();
        var dist = Assert.Single(roots, r => r.Path.Equals(DefaultDist, StringComparison.OrdinalIgnoreCase));
        var tools = Assert.Single(roots, r => r.Path.Equals(DefaultTools, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(recognised, dist.Recognises(new ToolRootChild(name, ChildKind.File)));
        Assert.False(dist.Recognises(new ToolRootChild(name, ChildKind.Folder)));
        Assert.False(dist.Recognises(new ToolRootChild(name, ChildKind.Link)));
        Assert.True(tools.RecognisesFolder("dist"));
        Assert.False(tools.RecognisesFolder("tools"));
        Assert.False(tools.RecognisesFolder("python_env"));
        Assert.False(tools.Recognises(new ToolRootChild("dist", ChildKind.Link)));
    }

    /// <summary>
    /// §7.1 as Explore applies it: an archive may go, but <c>dist</c> itself may not, because removing it
    /// would take the files in it nobody classified.
    /// </summary>
    [Fact]
    public async Task ExploreRemovesAnArchiveButNeverDistItself()
    {
        var (archives, kept) = CreateToolsFolder(DefaultTools);

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await CreateProvider().DiscoverToolRootsAsync());

        Assert.True(policy.MayRemove(archives[0]).IsAllowed, policy.MayRemove(archives[0]).Reason);
        Assert.False(policy.MayRemove(DefaultDist).IsAllowed);
        Assert.False(policy.MayRemove(kept[1]).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(DefaultTools, "tools")).IsAllowed);
    }

    /// <summary>A link in <c>dist</c> carrying an archive's name is not an archive the installer wrote, so it is left alone.</summary>
    [Fact]
    public async Task AFileLinkWithAnArchivesNameIsLeftAloneAndProtected()
    {
        var target = Write(Path.Combine(_temp.Path, "elsewhere", "target.zip"));
        var link = Path.Combine(DefaultDist, Toolchain);
        Directory.CreateDirectory(DefaultDist);
        SymbolicLink.ToFile(link, target);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        AssertProtected(plan, link);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(target));
    }

    /// <summary>A linked <c>dist</c> is never looked through, so §5.6 is not asked of a tree reached through a link.</summary>
    [Fact]
    public async Task ALinkedDistIsLeftAlone()
    {
        var target = Path.Combine(_temp.Path, "elsewhere", "dist");
        var archive = Write(Path.Combine(target, Toolchain));
        Directory.CreateDirectory(DefaultTools);
        Junction.ToDirectory(DefaultDist, target);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(archive));
    }

    /// <summary>A refusal is never read as an absent <c>dist</c>, and never as one that is clear.</summary>
    [Fact]
    public async Task ARefusedDistIsReportedAsUnreachedRatherThanAbsent()
    {
        CreateToolsFolder(DefaultTools);

        using (DeniedDirectory.WithUnreadableAttributes(DefaultDist))
        {
            var provider = CreateProvider();
            var plan = await provider.PlanAsync();

            Assert.True(await provider.IsPresentAsync());
            Assert.Empty(plan.Steps);
            Assert.True(plan.HasUnreadableRoot);
        }
    }

    [Fact]
    public async Task ADistThatWillNotBeListedOffersNothing()
    {
        CreateToolsFolder(DefaultTools);

        using (new DeniedDirectory(DefaultDist))
        {
            var plan = await CreateProvider().PlanAsync();

            Assert.Empty(plan.Steps);
            Assert.True(plan.HasUnreadableRoot);
            Assert.True(plan.WasNotExamined);
        }
    }

    /// <summary>A <c>dist</c> with nothing in it is ESP-IDF on the machine with nothing to reclaim, said as such.</summary>
    [Fact]
    public async Task AnEmptyDistSaysNothingIsKept()
    {
        Directory.CreateDirectory(DefaultDist);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("no downloaded tool archives", StringComparison.Ordinal));
    }

    /// <summary>A duplicate search must know that each <c>dist</c> is a place this row deletes from.</summary>
    [Fact]
    public async Task EachDistIsACleanedPlace()
    {
        CreateToolsFolder(DefaultTools);
        Write(Path.Combine(ManagerDist, Toolchain));

        var places = await CreateProvider().CleanedPlacesAsync();

        Assert.Equal(
            new[] { DefaultDist, ManagerDist }.Order(StringComparer.OrdinalIgnoreCase),
            places.Select(place => place.Path).Order(StringComparer.OrdinalIgnoreCase));
    }
}
