using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Pulumi's plugin cache is removed one plugin at a time, by <c>pulumi plugin rm</c> where Pulumi is
/// installed and by path where it is not. The rules worth proving are that the command names exactly one
/// plugin, that everything beside the plugins offered survives (§5.6), the access tokens and a local
/// stack's state first, that anything not named as Pulumi names its plugins is left alone (§5.2), that
/// nothing is offered while Pulumi runs, that a plugin a program runs from is held back at the preview
/// and at the clean, and that a plugin removed by path goes whole or not at all.
/// </summary>
public sealed class PulumiPluginProviderTests : IDisposable
{
    private const string Aws = "resource-aws-v6.0.0";

    private const string AwsNext = "resource-aws-v6.1.0";

    private const string Kubernetes = "resource-kubernetes-v4.18.1";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessInspector _processes = FakeProcessInspector.NothingRunning;
    private readonly FakeLiveTreeInspector _liveTrees = FakeLiveTreeInspector.NothingLive;

    public PulumiPluginProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _environment.WithExecutable("pulumi", Pulumi);
        _runner.Replying(Pulumi, Reply);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The <c>pulumi</c> the fake answers for.</summary>
    private string Pulumi => Path.Combine(Home, "bin", "pulumi.exe");

    private string Home => Path.Combine(_environment.UserProfile, ".pulumi");

    private string Cache => Path.Combine(Home, "plugins");

    /// <summary>What <c>pulumi plugin rm</c> does besides removing its plugin, where a test wants more.</summary>
    private Action? OnRemove { get; set; }

    private PulumiPluginProvider CreateProvider(FakeUserEnvironment? environment = null) =>
        new(environment ?? _environment, _runner, _processes, liveTrees: _liveTrees);

    private string Folder(string name) => Path.Combine(Cache, name);

    /// <summary>
    /// Pulumi's folder as Pulumi leaves it, with the access tokens, a local stack's state and the CLI
    /// beside the cache, and each plugin installed with the files Pulumi keeps beside it.
    /// </summary>
    private void Install(params string[] plugins)
    {
        Directory.CreateDirectory(Cache);
        File.WriteAllText(Path.Combine(Home, "credentials.json"), "{\"accessTokens\":{\"https://api.pulumi.com\":\"<YOUR_API_KEY>\"}}");
        Directory.CreateDirectory(Path.Combine(Home, "stacks", "dev"));
        Directory.CreateDirectory(Path.Combine(Home, "history"));
        Directory.CreateDirectory(Path.Combine(Home, "bin"));
        File.WriteAllBytes(Pulumi, new byte[1024]);

        foreach (var plugin in plugins)
        {
            InstallPlugin(plugin);
        }
    }

    private void InstallPlugin(string name)
    {
        var folder = Folder(name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"pulumi-{name[..name.LastIndexOf("-v", StringComparison.Ordinal)]}.exe"), new byte[8192]);
        File.WriteAllText(folder + ".lock", string.Empty);
        File.WriteAllText(folder + ".lastused", string.Empty);
        Age(folder);
    }

    /// <summary>Every file of a plugin last changed a month ago, so the guard on recent files has nothing to say about it.</summary>
    private static void Age(string folder)
    {
        var then = DateTime.UtcNow.AddDays(-30);

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Append(folder + ".lock").Append(folder + ".lastused"))
        {
            if (File.Exists(file))
            {
                File.SetCreationTimeUtc(file, then);
                File.SetLastWriteTimeUtc(file, then);
            }
        }
    }

    /// <summary>
    /// <c>pulumi plugin rm</c> as <c>PluginInfo.Delete</c> carries it out: the folder that matches, then
    /// its files.
    /// </summary>
    private CommandOutcome? Reply(string arguments)
    {
        var parts = arguments.Split(' ');

        if (parts is ["plugin", "rm", var kind, var name, var version, "--yes", "--non-interactive"])
        {
            var folder = Folder($"{kind}-{name}-v{version}");
            Directory.Delete(folder, recursive: true);
            File.Delete(folder + ".lock");
            File.Delete(folder + ".lastused");
            OnRemove?.Invoke();
            return new CommandOutcome(0, $"removed: {kind} {name}-{version}\n", string.Empty);
        }

        return null;
    }

    [Fact]
    public async Task IsPresentWithACacheWhetherOrNotPulumiIsInstalled()
    {
        Assert.False(await CreateProvider().IsPresentAsync());

        Install(Aws);

        Assert.True(await CreateProvider().IsPresentAsync());
        Assert.True(await CreateProvider(new FakeUserEnvironment(_temp.Path)).IsPresentAsync());
    }

    [Fact]
    public async Task OffersEachPluginThroughPulumisOwnRemove()
    {
        Install(Aws, AwsNext, Kubernetes);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Empty(plan.TargetedPaths);

        var steps = plan.Steps.Cast<RunCommandStep>().ToDictionary(step => step.Identity!.Key);
        Assert.Equal(new[] { Aws, AwsNext, Kubernetes }.Order(StringComparer.Ordinal), steps.Keys.Order(StringComparer.Ordinal));

        var aws = steps[Aws];
        Assert.Equal(Pulumi, aws.FileName);
        Assert.Equal("plugin rm resource aws 6.0.0 --yes --non-interactive", aws.Arguments);
        Assert.Equal(Folder(Aws), aws.Removes);
        Assert.Equal([Folder(Aws)], aws.MeasuredPaths);
        Assert.Equal(8192, aws.EstimatedBytes);
        Assert.Equal(CancelledCommand.Finishes, aws.WhenCancelled);
        Assert.NotNull(aws.TargetCheck);
        Assert.Equal(new ItemIdentity(Aws, "resource aws 6.0.0"), aws.Identity);
        Assert.Equal("aws (resource)", aws.Group);
        Assert.Equal("aws (resource)", steps[AwsNext].Group);
        Assert.Equal([new ItemFacet("Version", "6.0.0")], aws.Facets);
        Assert.NotNull(aws.LastWritten);
        Assert.True(aws.LastWritten < DateTime.UtcNow.AddDays(-29));
    }

    /// <summary>§6.3 by the form of the path: what a step and §5.6 name is written as the user would write it.</summary>
    [Fact]
    public async Task NamesEveryPathInDisplayForm()
    {
        Install(Aws);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.Cast<RunCommandStep>());
        Assert.DoesNotContain(@"\\?\", step.Removes!, StringComparison.Ordinal);
        Assert.All(step.MeasuredPaths, path => Assert.DoesNotContain(@"\\?\", path, StringComparison.Ordinal));
        Assert.All(plan.ProtectedPaths, path => Assert.DoesNotContain(@"\\?\", path.Path, StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2 and §5.6: everything in the cache that is not named as Pulumi names its plugins, a plugin
    /// still installing, the access tokens, a local stack's state and the CLI are asserted standing, and
    /// none is a step's subject.
    /// </summary>
    [Fact]
    public async Task ProtectsEverythingBesideThePluginsOffered()
    {
        Install(Aws);
        string[] unrecognised = ["resource-aws-v6.0", "widget-thing-v1.0.0", "resource-gcp-v8.0.0.tmp123456", "notes.txt"];

        foreach (var name in unrecognised.Where(name => !name.EndsWith(".txt", StringComparison.Ordinal)))
        {
            Directory.CreateDirectory(Folder(name));
        }

        File.WriteAllText(Folder("notes.txt"), "mine");
        InstallPlugin(Kubernetes);
        File.WriteAllText(Folder(Kubernetes) + ".partial", string.Empty);

        var plan = await CreateProvider(new FakeUserEnvironment(_temp.Path)).PlanAsync();

        foreach (var survivor in unrecognised.Select(Folder).Concat(
                 [
                     Cache, Home, Folder(Kubernetes), Folder(Kubernetes) + ".partial", Folder(Kubernetes) + ".lock",
                     Path.Combine(Home, "credentials.json"), Path.Combine(Home, "stacks"), Path.Combine(Home, "history"), Path.Combine(Home, "bin"),
                 ]))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
            Assert.DoesNotContain(plan.Steps, step => step.Subjects.Contains(survivor, StringComparer.OrdinalIgnoreCase));
        }

        Assert.Equal(
            [Folder(Aws), Folder(Aws) + ".lock", Folder(Aws) + ".lastused"],
            plan.Steps.SelectMany(step => step.Subjects));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(Folder(Aws), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The run passes when Pulumi removes only the plugin chosen, and the tokens and state survive.</summary>
    [Fact]
    public async Task RemovesThePluginAndVerifiesTheSurvivors()
    {
        Install(Aws, AwsNext);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(await plan.NarrowedToAsync([plan.Steps.Single(step => step.Identity!.Key == Aws)]));

        var outcome = Assert.Single(result.Steps);
        Assert.True(outcome.Succeeded);
        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.False(Directory.Exists(Folder(Aws)));
        Assert.True(Directory.Exists(Folder(AwsNext)));
        Assert.True(File.Exists(Path.Combine(Home, "credentials.json")));
        Assert.Single(_runner.Invocations, invocation => invocation.Arguments.StartsWith("plugin rm ", StringComparison.Ordinal));
    }

    /// <summary>§5.6 bites: a removal that also took the tokens, a stack's state or another plugin fails the run.</summary>
    [Theory]
    [InlineData("credentials.json")]
    [InlineData("stacks")]
    [InlineData(@"plugins\resource-aws-v6.1.0")]
    public async Task FailsTheRunWhenTheRemovalReachesBeyondThePlugin(string beside)
    {
        Install(Aws, AwsNext);
        var path = Path.Combine(Home, beside);
        OnRemove = () =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                Directory.Delete(path, recursive: true);
            }
        };

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(await plan.NarrowedToAsync([plan.Steps.Single(step => step.Identity!.Key == Aws)]));

        Assert.NotNull(result.Verification);
        Assert.False(result.Verification.Passed);
        Assert.Contains(result.Verification.Failures, check => check.Subject.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A deployment may be about to start a plugin it has chosen, so while Pulumi runs nothing is offered,
    /// the row says why rather than reading as clear, and Explore refuses the whole cache.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhilePulumiRuns()
    {
        Install(Aws);
        _processes.WithRunning("pulumi");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning && note.Message.Contains("Pulumi is running", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase));

        var refused = Assert.Single(await provider.DiscoverToolRootsAsync(), root => root.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
        Assert.False(refused.Recognises(new ToolRootChild(Aws, ChildKind.Folder)));
    }

    /// <summary>Pulumi started while the preview is on screen stops the command, and the plugin stays.</summary>
    [Fact]
    public async Task DoesNotRunTheRemoveOncePulumiHasStarted()
    {
        Install(Aws);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _processes.WithRunning("pulumi").PassTheReadingLimit();

        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("Pulumi is running", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Invocations);
        Assert.True(Directory.Exists(Folder(Aws)));
    }

    /// <summary>
    /// A plugin a program runs from is held back at the preview and refused in Explore, and one started
    /// after the preview stops its command at the clean.
    /// </summary>
    [Fact]
    public async Task HoldsBackAPluginAProgramRunsFrom()
    {
        Install(Aws, AwsNext);
        _liveTrees.WithProgram("pulumi-resource-aws.exe", executable: Path.Combine(Folder(Aws), "pulumi-resource-aws.exe"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(AwsNext, Assert.Single(plan.Steps).Identity!.Key);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase) && p.Reason == PulumiPluginUse.InUseReason);
        Assert.Contains(await provider.DiscoverToolRootsAsync(), root => root.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase));

        _liveTrees.WithProgram("pulumi-resource-aws.exe", executable: Path.Combine(Folder(AwsNext), "pulumi-resource-aws.exe")).PassTheReadingLimit();

        var outcome = Assert.Single((await provider.ExecuteAsync(plan)).Steps);
        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
        Assert.True(Directory.Exists(Folder(AwsNext)));
    }

    /// <summary>A check for use that cannot be asked in full offers nothing, and says so.</summary>
    [Fact]
    public async Task OffersNothingWhereUseCannotBeChecked()
    {
        Install(Aws);

        var plan = await new PulumiPluginProvider(_environment, _runner, _processes, liveTrees: FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase) && p.Reason == LiveTreeVeto.UncheckedReason);
    }

    /// <summary>
    /// Pulumi's remove would take both of two folders it reads as one plugin, so neither is offered by
    /// command, and one such folder written after the preview stops the command at the clean.
    /// </summary>
    [Fact]
    public async Task LeavesAPluginAloneWherePulumisRemoveWouldTakeAnotherFolder()
    {
        Install(Aws, Kubernetes);
        Directory.CreateDirectory(Folder("resource-aws-v6.0.0+build.7"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Kubernetes, Assert.Single(plan.Steps).Identity!.Key);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(Folder("resource-kubernetes-v4.18"));

        var outcome = Assert.Single((await provider.ExecuteAsync(plan)).Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("resource-kubernetes-v4.18", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>A plugin whose folder became a link after the preview is not removed through it.</summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DoesNotRunTheRemoveThroughALinkMadeSinceThePreview(DirectoryLinkKind kind)
    {
        Install(Aws);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "mine");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        Directory.Delete(Folder(Aws), recursive: true);
        DirectoryLink.Create(kind, Folder(Aws), elsewhere);

        var outcome = Assert.Single((await provider.ExecuteAsync(plan)).Steps);
        Assert.False(outcome.Succeeded);
        Assert.Empty(_runner.Invocations);
        Assert.True(File.Exists(Path.Combine(elsewhere, "keep.txt")));
    }

    /// <summary>
    /// A cache moved elsewhere and replaced by a link after the preview still lists the plugin, so only
    /// asking whether the cache is a link stops the command running through it.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DoesNotRunTheRemoveThroughACacheThatBecameALink(DirectoryLinkKind kind)
    {
        Install(Aws);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var moved = Path.Combine(_temp.Path, "elsewhere");
        Directory.Move(Cache, moved);
        DirectoryLink.Create(kind, Cache, moved);

        var outcome = Assert.Single((await provider.ExecuteAsync(plan)).Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("link", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Invocations);
        Assert.True(Directory.Exists(Path.Combine(moved, Aws)));
    }

    /// <summary>A cache that is a link, or a plugin folder that is one, is not looked through.</summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DoesNotLookThroughALink(DirectoryLinkKind kind)
    {
        Install();
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        DirectoryLink.Create(kind, Folder(Aws), elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase));

        Directory.Delete(Folder(Aws));
        Directory.Delete(Cache);
        Directory.CreateDirectory(Path.Combine(elsewhere, Aws));
        DirectoryLink.Create(kind, Cache, elsewhere);

        var linked = await CreateProvider().PlanAsync();

        Assert.Empty(linked.Steps);
        Assert.True(linked.WasNotExamined);
    }

    /// <summary>
    /// <c>PULUMI_HOME</c> moves the cache the command acts on, and a value naming the profile itself is
    /// declined rather than examined as Pulumi's.
    /// </summary>
    [Fact]
    public async Task FollowsPulumiHomeAndDeclinesTheProfile()
    {
        var moved = Path.Combine(_temp.Path, "tools", "pulumi");
        Directory.CreateDirectory(Path.Combine(moved, "plugins", Aws));
        Age(Path.Combine(moved, "plugins", Aws));

        var plan = await CreateProvider(_environment.WithEnvironmentVariable(PulumiPluginProvider.HomeVariable, moved)).PlanAsync();

        Assert.Equal(Path.Combine(moved, "plugins", Aws), Assert.Single(plan.Steps).Subjects.Single());

        var declined = new FakeUserEnvironment(_temp.Path)
            .WithExecutable("pulumi", Pulumi)
            .WithEnvironmentVariable(PulumiPluginProvider.HomeVariable, _environment.UserProfile);
        Directory.CreateDirectory(Path.Combine(_environment.UserProfile, "plugins", Aws));

        var refused = await CreateProvider(declined).PlanAsync();

        Assert.Empty(refused.Steps);
        Assert.True(refused.WasNotExamined);
        Assert.Contains(await CreateProvider(declined).DiscoverToolRootsAsync(), root =>
            root.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase)
            && !root.Recognises(new ToolRootChild("credentials.json", ChildKind.File))
            && root.Recognises(new ToolRootChild("Documents", ChildKind.Folder)));
    }

    /// <summary>
    /// §5.2 for Explore: Pulumi's folder opens only the way to the cache, and the cache recognises only
    /// the plugins and the files beside them.
    /// </summary>
    [Fact]
    public void ExploreRecognisesOnlyThePluginsAndTheirFiles()
    {
        var roots = CreateProvider().ToolRoots;

        var home = Assert.Single(roots, root => root.Path.Equals(Home, StringComparison.OrdinalIgnoreCase));
        Assert.True(home.Recognises(new ToolRootChild("plugins", ChildKind.Folder)));
        Assert.False(home.Recognises(new ToolRootChild("credentials.json", ChildKind.File)));
        Assert.False(home.Recognises(new ToolRootChild("stacks", ChildKind.Folder)));

        var cache = Assert.Single(roots, root => root.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
        Assert.True(cache.Recognises(new ToolRootChild(Aws, ChildKind.Folder)));
        Assert.True(cache.Recognises(new ToolRootChild(Aws + ".lock", ChildKind.File)));
        Assert.True(cache.Recognises(new ToolRootChild(Aws + ".lastused", ChildKind.File)));
        Assert.False(cache.Recognises(new ToolRootChild(Aws + ".partial", ChildKind.File)));
        Assert.False(cache.Recognises(new ToolRootChild(Aws, ChildKind.Link)));
        Assert.False(cache.Recognises(new ToolRootChild(Aws, ChildKind.File)));
        Assert.False(cache.Recognises(new ToolRootChild("resource-aws-v6.0", ChildKind.Folder)));
        Assert.False(cache.Recognises(new ToolRootChild("notes.txt", ChildKind.File)));
    }

    /// <summary>
    /// A plugin Pulumi has not finished installing has a name like any other, and only the file beside
    /// it tells it apart, so Explore refuses its folder and the files beside it from what is on the disk.
    /// </summary>
    [Fact]
    public void ExploreRefusesAPluginStillInstalling()
    {
        Install(Aws, Kubernetes);
        File.WriteAllText(Folder(Kubernetes) + ".partial", string.Empty);

        var cache = Assert.Single(CreateProvider().ToolRoots, root => root.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));

        Assert.False(cache.Recognises(new ToolRootChild(Kubernetes, ChildKind.Folder)));
        Assert.False(cache.Recognises(new ToolRootChild(Kubernetes + ".lock", ChildKind.File)));
        Assert.True(cache.Recognises(new ToolRootChild(Aws, ChildKind.Folder)));
        Assert.True(cache.Recognises(new ToolRootChild(Aws + ".lock", ChildKind.File)));
    }

    /// <summary>
    /// Where what runs from the cache cannot be read in full, the plan holds every plugin back, and so
    /// Explore refuses the whole cache.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheCacheWhereUseCannotBeChecked()
    {
        Install(Aws);

        var roots = await new PulumiPluginProvider(_environment, _runner, _processes, liveTrees: FakeLiveTreeInspector.CannotTell)
            .DiscoverToolRootsAsync();

        var refused = Assert.Single(roots, root => root.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LiveTreeVeto.UncheckedReason, refused.Reason);
        Assert.False(refused.Recognises(new ToolRootChild(Aws, ChildKind.Folder)));
        Assert.DoesNotContain(await CreateProvider().DiscoverToolRootsAsync(), root => root.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Without Pulumi each plugin is removed by path, its folder whole or not at all and then the files
    /// beside it, all one item, and the run removes exactly those.
    /// </summary>
    [Fact]
    public async Task RemovesAPluginByPathWherePulumiIsNotInstalled()
    {
        Install(Aws, AwsNext);
        var provider = CreateProvider(new FakeUserEnvironment(_temp.Path));

        var plan = await provider.PlanAsync();

        var aws = plan.Steps.Where(step => step.Identity!.Key == Aws).ToList();
        Assert.Equal([Folder(Aws), Folder(Aws) + ".lock", Folder(Aws) + ".lastused"], aws.SelectMany(step => step.Subjects));
        Assert.True(Assert.IsType<DeleteDirectoryStep>(aws[0]).IsAllOrNothing);
        Assert.All(aws.Skip(1), step => Assert.True(Assert.IsType<DeleteFileStep>(step).IsLeftover));
        Assert.Contains(plan.Notes, note => note.Message.Contains("Pulumi is not installed", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(await plan.NarrowedToAsync(aws));

        Assert.NotNull(result.Verification);
        Assert.True(result.Verification.Passed);
        Assert.False(Directory.Exists(Folder(Aws)));
        Assert.False(File.Exists(Folder(Aws) + ".lastused"));
        Assert.True(Directory.Exists(Folder(AwsNext)));
        Assert.True(File.Exists(Path.Combine(Home, "credentials.json")));
    }

    /// <summary>
    /// The guard on recently changed files keeps a plugin used recently whole: a removal that kept only
    /// its <c>.lastused</c>, or one recent file in its folder, would leave something Pulumi misreads.
    /// </summary>
    [Theory]
    [InlineData(".lastused")]
    [InlineData(@"\pulumi-resource-aws.exe")]
    public async Task KeepsARecentlyUsedPluginWholeWhenRemovingByPath(string touched)
    {
        Install(Aws, AwsNext);
        File.SetLastWriteTimeUtc(Folder(Aws) + touched, DateTime.UtcNow);

        var plan = await CreateProvider(new FakeUserEnvironment(_temp.Path)).PlanAsync(MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow));

        Assert.DoesNotContain(plan.Steps, step => step.Identity!.Key == Aws);
        Assert.Contains(plan.Steps, step => step.Identity!.Key == AwsNext);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Folder(Aws), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, note => note.Message.Contains("whole or not at all", StringComparison.Ordinal));
    }
}
