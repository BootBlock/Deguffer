using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two providers that remove build output from an Unreal project: its <c>Intermediate</c> and
/// its own <c>DerivedDataCache</c>.
///
/// <para>What is particular to Unreal is the evidence and the neighbours. A project is recognised by
/// a descriptor named after the project, so no fixed name can state it. And beside the build output
/// sit <c>Saved</c>, which holds the only copy of unsaved editor work, and <c>Binaries</c>, which on
/// a machine without a compiler cannot be rebuilt. Neither is ever a target.</para>
/// </summary>
public sealed class UnrealProjectProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly SourceRootStore _roots;

    public UnrealProjectProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _roots = new SourceRootStore(_environment);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PlansEachProjectsIntermediateAndItsDerivedDataAsSeparateRows()
    {
        var project = Path.Combine(ApproveRoot(), "Shooter");
        var intermediate = BuildDirectoryFixture.CreateUnrealProject(project);
        var derived = BuildDirectoryFixture.CreateUnrealProject(project, directoryName: "DerivedDataCache");

        var intermediatePlan = await Intermediate().PlanAsync();
        var derivedPlan = await DerivedData().PlanAsync();

        Assert.Equal([intermediate], intermediatePlan.TargetedPaths);
        Assert.Equal([derived], derivedPlan.TargetedPaths);
        Assert.Equal(SafetyTier.RegenerableWithCost, intermediatePlan.Tier);
        Assert.Equal(SafetyTier.RegenerableWithCost, derivedPlan.Tier);
    }

    /// <summary>
    /// §5.6, run for real. Both providers remove their folder from one project, and everything the
    /// project is made of survives: the descriptor, which only this project's own name can state,
    /// the source, the content, the settings, the compiled game, and the autosaves above all.
    /// </summary>
    [Fact]
    public async Task EverythingTheProjectIsMadeOfSurvivesBothRemovals()
    {
        var project = Path.Combine(ApproveRoot(), "Shooter");
        var intermediate = BuildDirectoryFixture.CreateUnrealProject(project, descriptor: "Shooter.uproject");
        var derived = BuildDirectoryFixture.CreateUnrealProject(project, directoryName: "DerivedDataCache", descriptor: null);

        string[] survivors =
        [
            Path.Combine(project, "Shooter.uproject"),
            Path.Combine(project, "Saved"),
            Path.Combine(project, "Binaries"),
            Path.Combine(project, "Source"),
            Path.Combine(project, "Content"),
            Path.Combine(project, "Config"),
        ];

        foreach (var provider in new BuildDirectoryProvider[] { Intermediate(), DerivedData() })
        {
            var plan = await provider.PlanAsync();

            foreach (var survivor in survivors)
            {
                Assert.Contains(plan.ProtectedPaths, p =>
                    p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            }

            Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
            Assert.True((await provider.VerifyAsync(plan)).Passed);
        }

        Assert.False(Directory.Exists(intermediate));
        Assert.False(Directory.Exists(derived));
        Assert.True(File.Exists(Path.Combine(project, "Shooter.uproject")));
        Assert.True(File.Exists(Path.Combine(project, "Saved", "Autosaves", "Main_Auto1.umap")));
        Assert.True(File.Exists(Path.Combine(project, "Binaries", "Win64", "UnrealEditor-Game.dll")));
        Assert.True(File.Exists(Path.Combine(project, "Source", "Game", "Game.cpp")));
        Assert.True(File.Exists(Path.Combine(project, "Content", "Maps", "Main.umap")));
        Assert.True(File.Exists(Path.Combine(project, "Config", "DefaultEngine.ini")));
    }

    /// <summary>
    /// §5.2. A folder called <c>Intermediate</c> is an ordinary word without the descriptor beside
    /// it, and a plugin's or an engine's has a different descriptor or none. Each is left alone.
    /// </summary>
    [Fact]
    public async Task AnIntermediateWithoutAProjectDescriptorBesideItIsNeverATarget()
    {
        var root = ApproveRoot();
        var project = Path.Combine(root, "Shooter");
        var intermediate = BuildDirectoryFixture.CreateUnrealProject(project);
        var plugin = BuildDirectoryFixture.CreateUnrealProject(
            Path.Combine(project, "Plugins", "Weather"), descriptor: "Weather.uplugin");
        var engine = BuildDirectoryFixture.CreateUnrealProject(Path.Combine(root, "UE5", "Engine"), descriptor: null);

        var plan = await Intermediate().PlanAsync();

        Assert.Equal([intermediate], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(engine, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The descriptor is matched by its own extension. A backup of it, or a name that only begins
    /// with the extension, is not a project.
    /// </summary>
    [Theory]
    [InlineData("Shooter.uproject.bak")]
    [InlineData("Shooter.uprojectx")]
    [InlineData("uproject")]
    public async Task OnlyAFileEndingInTheDescriptorsExtensionRecognisesTheProject(string lookalike)
    {
        var project = Path.Combine(ApproveRoot(), "Shooter");
        var intermediate = BuildDirectoryFixture.CreateUnrealProject(project, descriptor: lookalike);

        var plan = await Intermediate().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(intermediate));
    }

    [Fact]
    public async Task TheDescriptorsNameIsTheProjectsOwnAndAnyNameRecognisesIt()
    {
        var project = Path.Combine(ApproveRoot(), "Shooter");
        var intermediate = BuildDirectoryFixture.CreateUnrealProject(project, descriptor: "Anything At All.UPROJECT");

        Assert.Equal([intermediate], (await Intermediate().PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// §5.3. The editor works in the engine's folder, so the veto on a project in use cannot see it,
    /// and a running editor is said out loud beside what is offered.
    /// </summary>
    [Fact]
    public async Task ARunningEditorIsAWarningBesideWhatIsOffered()
    {
        BuildDirectoryFixture.CreateUnrealProject(Path.Combine(ApproveRoot(), "Shooter"));

        var plan = await Intermediate(new FakeProcessInspector("UnrealEditor")).PlanAsync();

        Assert.Single(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("UnrealEditor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoEditorWarningWhereNothingIsOffered()
    {
        ApproveRoot();

        var plan = await Intermediate(new FakeProcessInspector("UnrealEditor")).PlanAsync();

        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("UnrealEditor", StringComparison.Ordinal));
    }

    private string ApproveRoot()
    {
        var root = _temp.CreateDirectory("src");
        _roots.Save([new SourceRoot(root)]);
        return root;
    }

    private UnrealIntermediateProvider Intermediate(IProcessInspector? inspector = null) =>
        new(_roots, Discovery(), FakeLiveTreeInspector.NothingLive, _environment,
            new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, new FakeDirectoryScanner());

    private UnrealProjectDerivedDataProvider DerivedData() =>
        new(_roots, Discovery(), FakeLiveTreeInspector.NothingLive, _environment,
            new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner());

    private static SourceDirectoryDiscovery Discovery() =>
        new(new FakeDirectoryScanner(), new FakeVolumeInventory());
}
