using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The folders a Code - OSS editor leaves in its extensions folder from installs and removals it never
/// finished. The rules worth proving are that a folder is entered only where the editor's own list
/// identifies it, that only the two leftover shapes are offered and never a folder the list names,
/// that a folder goes whole and only once nothing in it has changed for a week, and §5.6's, that every
/// installed extension, the editor's records and everything unrecognised survives.
/// </summary>
public sealed class VsCodeExtensionLeftoversProviderTests : IDisposable
{
    private const string OldInstall = ".0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string FreshInstall = ".11111111-2222-4333-8444-555555555555";
    private const string ListedInstall = ".99999999-8888-4777-8666-555555555555";
    private const string SetAside = "ms-python.python-2025.1.0.-1a2b3c.vsctmp";
    private const string Installed = "ms-python.python-2025.2.0";
    private const string OtherInstalled = "esbenp.prettier-vscode-11.0.0";

    private static readonly TimeSpan Old = TimeSpan.FromDays(VsCodeExtensionLeftoversProvider.QuietDays + 30);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public VsCodeExtensionLeftoversProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private VsCodeExtensionLeftoversProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning,
            system: _system, liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive);

    private string Extensions(string dotFolder = ".vscode") => Path.Combine(_environment.UserProfile, dotFolder, "extensions");

    /// <summary>
    /// An extensions folder holding the installed extensions <paramref name="installed"/> names, each
    /// listed in the editor's list by every form the editor writes a location in.
    /// </summary>
    private static string CreateEditorFolder(string folder, params string[] installed)
    {
        foreach (var name in installed)
        {
            CreateFile(Path.Combine(folder, name, "package.json"));
        }

        WriteList(folder, installed.Select(name => Entry(name, Path.Combine(folder, name))).ToArray());
        CreateFile(Path.Combine(folder, ".obsolete"));
        return folder;
    }

    private static object Entry(string name, string location) => new
    {
        identifier = new { id = name.Split('-')[0] },
        version = "1.0.0",
        location = new { fsPath = location, scheme = "file" },
        relativeLocation = Path.GetFileName(location),
    };

    private static void WriteList(string folder, object[] entries)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "extensions.json"), JsonSerializer.Serialize(entries));
    }

    /// <summary>A full extension extracted into <paramref name="name"/>, every entry in it dated <paramref name="age"/> ago.</summary>
    private static string CreateLeftover(string folder, string name, TimeSpan age)
    {
        var path = Path.Combine(folder, name);
        CreateFile(Path.Combine(path, "package.json"));
        CreateFile(Path.Combine(path, ".vsixmanifest"));
        CreateFile(Path.Combine(path, "dist", "extension.js"), 8192);
        AgeTree(path, age);
        return path;
    }

    /// <summary>Every entry in the tree and the tree's own folder, dated <paramref name="age"/> ago.</summary>
    private static void AgeTree(string path, TimeSpan age)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            TempDirectory.Age(entry, age);
        }

        TempDirectory.Age(path, age);
    }

    private static string CreateFile(string path, int bytes = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task AnEditorWithNothingLeftOverIsNotARow()
    {
        CreateEditorFolder(Extensions(), Installed);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
    }

    /// <summary>
    /// The issue's whole fixture: of everything in the folder, only the old unfinished install and the
    /// old set-aside removal are planned.
    /// </summary>
    [Fact]
    public async Task OffersOnlyTheOldUnfinishedInstallAndTheOldSetAsideRemoval()
    {
        var folder = CreateEditorFolder(Extensions(), Installed, OtherInstalled);
        var old = CreateLeftover(folder, OldInstall, Old);
        var setAside = CreateLeftover(folder, SetAside, Old);
        CreateLeftover(folder, FreshInstall, TimeSpan.Zero);
        CreateLeftover(folder, ".not-a-uuid", Old);
        CreateLeftover(folder, ".ABCDEF01-2345-4678-89AB-CDEF01234567", Old);
        CreateLeftover(folder, "renamed.vsctmp", Old);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([old, setAside], plan.TargetedPaths.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A folder in the leftover shape that the editor's list names is an installed extension, whichever
    /// form of the location names it.
    /// </summary>
    [Theory]
    [InlineData("fsPath")]
    [InlineData("path")]
    [InlineData("relativeLocation")]
    [InlineData("string")]
    public async Task NeverOffersAFolderTheEditorsListNames(string form)
    {
        var folder = Extensions();
        var listed = CreateLeftover(folder, ListedInstall, Old);
        var forward = "/" + listed.Replace('\\', '/');

        object entry = form switch
        {
            "fsPath" => new { identifier = new { id = "a.b" }, location = new { fsPath = listed } },
            "path" => new { identifier = new { id = "a.b" }, location = new { path = forward, scheme = "file" } },
            "relativeLocation" => new { identifier = new { id = "a.b" }, location = new { scheme = "file" }, relativeLocation = ListedInstall },
            _ => new { identifier = new { id = "a.b" }, location = listed },
        };

        WriteList(folder, [entry]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == listed && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>§5.6: the clean removes the two leftovers, and everything else in the folder survives and is proven to.</summary>
    [Fact]
    public async Task TheCleanLeavesEveryInstalledExtensionAndTheEditorsRecordsStanding()
    {
        var folder = CreateEditorFolder(Extensions(), Installed, OtherInstalled);
        var old = CreateLeftover(folder, OldInstall, Old);
        var setAside = CreateLeftover(folder, SetAside, Old);
        var unrecognised = CreateLeftover(folder, ".not-a-uuid", Old);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        string[] survivors =
        [
            folder,
            Path.Combine(folder, Installed),
            Path.Combine(folder, OtherInstalled),
            Path.Combine(folder, "extensions.json"),
            Path.Combine(folder, ".obsolete"),
            unrecognised,
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == survivor && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(setAside));
        Assert.All(survivors, survivor => Assert.True(Path.Exists(survivor), survivor));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A junction named like an unfinished install is never followed: neither it nor what it points at is
    /// planned, and both are still there afterwards.
    /// </summary>
    [Fact]
    public async Task NeverFollowsALinkNamedLikeALeftover()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        var elsewhere = CreateLeftover(_temp.CreateDirectory("elsewhere"), "payload", Old);
        var link = Path.Combine(folder, OldInstall);
        Junction.ToDirectory(link, elsewhere);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == link);
        Assert.Contains(plan.Notes, n => n.Message.Contains(OldInstall, StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(elsewhere, "dist", "extension.js")));
    }

    /// <summary>
    /// An install in progress writes deep inside its folder, and its top level can still read old, so a
    /// recent file at any depth holds the whole folder back, and nothing in it is touched.
    /// </summary>
    [Fact]
    public async Task ARecentFileDeepInsideHoldsTheWholeFolderBack()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        var leftover = CreateLeftover(folder, OldInstall, Old);
        var nested = Path.Combine(leftover, "dist", "chunks");
        Directory.CreateDirectory(nested);
        CreateFile(Path.Combine(nested, "arriving.js"));
        TempDirectory.Age(nested, Old);
        TempDirectory.Age(Path.Combine(leftover, "dist"), Old);
        TempDirectory.Age(leftover, Old);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Left one folder alone", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(leftover, "package.json")));
    }

    /// <summary>
    /// An extraction that has only just begun has an empty folder, with no file for the measurement to
    /// find recent, so the folder's own times hold it back.
    /// </summary>
    [Fact]
    public async Task AnEmptyFolderAnInstallHasJustMadeIsNotOffered()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        Directory.CreateDirectory(Path.Combine(folder, FreshInstall));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// Whole or not at all holds at the clean as well: a file written since the preview stops the
    /// removal, and the older half stays too.
    /// </summary>
    [Fact]
    public async Task AFileWrittenAfterThePreviewStopsTheWholeFolder()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        var leftover = CreateLeftover(folder, OldInstall, Old);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        Assert.Equal([leftover], plan.TargetedPaths);

        var arrived = CreateFile(Path.Combine(leftover, "dist", "arrived.js"));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(arrived));
        Assert.True(File.Exists(Path.Combine(leftover, "package.json")));
        Assert.False(Assert.Single(result.Steps).Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task HoldsBackALeftoverARunningProgramIsUsing()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        var leftover = CreateLeftover(folder, OldInstall, Old);

        var plan = await CreateProvider(new FakeLiveTreeInspector(leftover)).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == leftover);
    }

    /// <summary>A folder is entered only where the editor's own list, in the editor's shape, identifies it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"identifier":{"id":"a.b"},"location":"x"}""")]
    [InlineData("""[{"identifier":{"id":"a.b"}}]""")]
    [InlineData("""[{"identifier":"a.b","location":"x"}]""")]
    [InlineData("""[{"identifier":{"id":"a.b"},"location":"x"},{"location":"y"}]""")]
    public async Task AFolderWithoutTheEditorsListIsNotEntered(string? list)
    {
        var folder = Extensions();
        CreateLeftover(folder, OldInstall, Old);

        if (list is not null)
        {
            File.WriteAllText(Path.Combine(folder, "extensions.json"), list);
        }

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>Every dot-folder is asked, so an editor Deguffer has never heard of is reached as VS Code is.</summary>
    [Fact]
    public async Task ReachesEveryEditorByItsOwnDotFolder()
    {
        var code = CreateLeftover(CreateEditorFolder(Extensions(), Installed), OldInstall, Old);
        var oss = CreateLeftover(CreateEditorFolder(Extensions(".some-other-editor"), Installed), OldInstall, Old);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([oss, code], plan.TargetedPaths.Order(StringComparer.Ordinal));
        Assert.Contains(plan.Steps.OfType<DeleteDirectoryStep>(), s => s.Path == oss && s.Group == ".some-other-editor");
    }

    [Fact]
    public async Task ReachesTheFolderTheVariableNames()
    {
        var moved = CreateEditorFolder(_temp.CreateDirectory("moved", "extensions"), Installed);
        var leftover = CreateLeftover(moved, OldInstall, Old);
        _environment.WithEnvironmentVariable("VSCODE_EXTENSIONS", moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([leftover], plan.TargetedPaths);
        Assert.Equal("VSCODE_EXTENSIONS", Assert.Single(plan.Steps).Group);
    }

    /// <summary>The variable is a setting anything may have written, so its folder must pass the same test.</summary>
    [Fact]
    public async Task IgnoresAVariableFolderWithoutTheEditorsList()
    {
        var moved = _temp.CreateDirectory("moved", "extensions");
        CreateLeftover(moved, OldInstall, Old);
        _environment.WithEnvironmentVariable("VSCODE_EXTENSIONS", moved);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task SaysWhyARelativeVariableIsNotFollowed()
    {
        _environment.WithEnvironmentVariable("VSCODE_EXTENSIONS", @"moved\extensions");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// A variable naming the profile itself is declined: the profile is never examined as the editor's,
    /// and only what the editor wrote there is refused to Explore.
    /// </summary>
    [Fact]
    public async Task DeclinesAVariableNamingTheProfileAndRefusesOnlyTheEditorsEntries()
    {
        CreateEditorFolder(_environment.UserProfile, Installed);
        var leftover = CreateLeftover(_environment.UserProfile, OldInstall, Old);
        _environment.WithEnvironmentVariable("VSCODE_EXTENSIONS", _environment.UserProfile);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(leftover, plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("VSCODE_EXTENSIONS", StringComparison.Ordinal));

        var root = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(_environment.UserProfile, root.Path);
        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild("extensions.json", ChildKind.File)));
        Assert.False(root.RecognisesFolder(Installed));
        Assert.True(root.RecognisesFolder("Documents"));
    }

    /// <summary>The variable naming the folder the profile already holds plans each leftover once.</summary>
    [Fact]
    public async Task PlansAFolderReachedTwiceOnce()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        var leftover = CreateLeftover(folder, OldInstall, Old);
        _environment.WithEnvironmentVariable("VSCODE_EXTENSIONS", folder);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([leftover], plan.TargetedPaths);
    }

    /// <summary>
    /// An extensions folder that is itself a junction is identified through, so the reader is told, and
    /// never entered: what it points at was never classified.
    /// </summary>
    [Fact]
    public async Task NamesALinkedExtensionsFolderAndNeverEntersIt()
    {
        var real = CreateEditorFolder(_temp.CreateDirectory("D", "extensions"), Installed);
        var leftover = CreateLeftover(real, OldInstall, Old);
        Directory.CreateDirectory(Path.Combine(_environment.UserProfile, ".vscode"));
        Junction.ToDirectory(Extensions(), real);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Extensions());
        Assert.True(await provider.IsPresentAsync());

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(leftover));
    }

    [Fact]
    public void IsTierOneBecauseTheEditorNeverReadsTheseFolders() =>
        Assert.Equal(SafetyTier.RegenerableCache, CreateProvider().Tier);

    /// <summary>
    /// A plan holds display paths, and each leftover is the leftover itself. The removal beneath extends
    /// each path where it acts (§6.3), which <c>DirectoryRemover</c>'s own tests assert by the form of the
    /// path it hands on.
    /// </summary>
    [Fact]
    public async Task EachStepIsALeftoverInDisplayForm()
    {
        CreateLeftover(CreateEditorFolder(Extensions(), Installed), OldInstall, Old);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));
        Assert.False(step.Path.StartsWith(@"\\?\", StringComparison.Ordinal), step.Path);
        Assert.True(step.IsLeftover);
        Assert.True(step.IsAllOrNothing);
        Assert.True(plan.Keep.IsOn);
    }

    [Fact]
    public void DeclaresOnlyTheLeftoverNamesInEachFolder()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        CreateLeftover(folder, OldInstall, Old);
        WriteList(folder, [Entry(Installed, Path.Combine(folder, Installed)), Entry("x", Path.Combine(folder, ListedInstall))]);

        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(folder, root.Path);
        Assert.True(root.RecognisesFolder(OldInstall));
        Assert.True(root.RecognisesFolder(SetAside));
        Assert.False(root.RecognisesFolder(Installed));
        Assert.False(root.RecognisesFolder(ListedInstall));
        Assert.False(root.RecognisesFolder(".not-a-uuid"));
    }

    [Fact]
    public async Task ItsCleanedPlacesCoverWhatItRemoves()
    {
        var folder = CreateEditorFolder(Extensions(), Installed);
        CreateLeftover(folder, OldInstall, Old);
        CreateLeftover(folder, SetAside, Old);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }
}
