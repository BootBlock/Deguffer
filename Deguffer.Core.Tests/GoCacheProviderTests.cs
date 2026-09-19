using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Go is a §5.1 provider, so the property that matters most is a negative one: it deletes no path
/// at all, and both locations are cleared by the commands Go ships. Everything here runs through
/// <see cref="FakeProcessRunner"/>, which is what lets <c>go env</c> answer whatever a case needs
/// on a machine with no Go toolchain installed.
/// </summary>
public sealed class GoCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public GoCacheProviderTests() =>
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("go");

    public void Dispose() => _temp.Dispose();

    private string GoPath => Path.Combine(_environment.UserProfile, "workspace");

    private string BuildCache => Path.Combine(_environment.LocalAppData, "go-build");

    private string ModuleCache => Path.Combine(GoPath, "pkg", "mod");

    /// <summary>A runner whose <c>go env</c> answers with the three locations, one per line.</summary>
    private FakeProcessRunner Reporting(string buildCache, string moduleCache, string goPath) =>
        new FakeProcessRunner().Responding("env", $"{buildCache}\r\n{moduleCache}\r\n{goPath}\r\n");

    private GoCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? Reporting(BuildCache, ModuleCache, GoPath), FakeProcessInspector.NothingRunning);

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    [Fact]
    public async Task ReportsNotPresentWhenGoIsNotInstalled()
    {
        var provider = new GoCacheProvider(
            new FakeUserEnvironment(_temp.Path), new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task PlansGosOwnCommandForEachLocationItReports()
    {
        Populate(BuildCache);
        Populate(ModuleCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            ["clean -cache", "clean -modcache"],
            plan.Steps.OfType<RunCommandStep>().Select(s => s.Arguments));
        Assert.Equal(
            [BuildCache, ModuleCache],
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths));
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §5.1's whole point, stated as the assertion it deserves. A path-based Go provider would meet
    /// the module cache's read-only files and reclaim nothing while reporting success, so this plan
    /// must target no path at all.
    /// </summary>
    [Fact]
    public async Task DeletesNoPathBecauseGoHasACommandForBothLocations()
    {
        Populate(BuildCache);
        Populate(ModuleCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
    }

    [Fact]
    public async Task PlansOnlyTheLocationThatIsActuallyThere()
    {
        Populate(BuildCache);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal("clean -cache", step.Arguments);
    }

    [Fact]
    public async Task SaysSoWhenGoIsInstalledButHasCachedNothing()
    {
        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("cached nothing yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cache Windows will not describe is reported as one Deguffer could not reach, never as
    /// absent. "Go is installed but has cached nothing yet" was said about a module cache holding
    /// 4 KB, and the row read as clear.
    /// </summary>
    [Fact]
    public async Task ACacheWindowsWillNotDescribeIsSaidToBeUnreachedRatherThanAbsent()
    {
        Populate(ModuleCache);

        using var denied = DeniedDirectory.WithUnreadableAttributes(ModuleCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Empty(plan.Steps);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(ModuleCache, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("cached nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// Beside a cache that is there, the refused one gets no step, because nothing could measure what
    /// clearing it would free, and it is named rather than dropped.
    /// </summary>
    [Fact]
    public async Task ACacheWindowsWillNotDescribeGetsNoStepAndIsNamedBesideTheOther()
    {
        Populate(BuildCache);
        Populate(ModuleCache);

        using var denied = DeniedDirectory.WithUnreadableAttributes(ModuleCache);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal("clean -cache", step.Arguments);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(ModuleCache, StringComparison.Ordinal));
    }

    /// <summary>
    /// The locations move independently, and only <c>go env</c> knows where they are. A provider
    /// that assumed the defaults would measure and clear the wrong directories on any machine whose
    /// owner had moved one.
    /// </summary>
    [Fact]
    public async Task HonoursTheLocationsGoReportsRatherThanTheDefaults()
    {
        var buildCache = Populate(Path.Combine(_temp.Path, "elsewhere", "build"));
        var moduleCache = Populate(Path.Combine(_temp.Path, "elsewhere", "mod"));

        var plan = await CreateProvider(Reporting(buildCache, moduleCache, GoPath)).PlanAsync();

        Assert.Equal(
            [buildCache, moduleCache],
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths));
    }

    /// <summary>
    /// <c>go env</c> prints one line per name asked for, in order, and an empty line for a value it
    /// has no answer to. Reading the lines positionally is what makes that work, and a value that is
    /// not a rooted path falls back to the documented default rather than being taken literally.
    /// </summary>
    [Fact]
    public async Task FallsBackToTheDocumentedDefaultsWhenGoAnswersWithNothing()
    {
        Populate(Path.Combine(_environment.LocalAppData, "go-build"));
        Populate(Path.Combine(_environment.UserProfile, "go", "pkg", "mod"));

        var plan = await CreateProvider(new FakeProcessRunner().Responding("env", "\r\n\r\n\r\n")).PlanAsync();

        Assert.Equal(
            [
                Path.Combine(_environment.LocalAppData, "go-build"),
                Path.Combine(_environment.UserProfile, "go", "pkg", "mod"),
            ],
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths));
    }

    /// <summary>
    /// "Go reports its build cache as X" is a claim about a subprocess that may never have spoken.
    /// When it did not, X is Deguffer's guess, and a machine whose caches have been moved will not
    /// match it — so the plan says which of the two it is holding.
    /// </summary>
    [Fact]
    public async Task SaysTheLocationsAreDefaultsWhenGoDidNotReportThem()
    {
        Populate(Path.Combine(_environment.LocalAppData, "go-build"));

        var plan = await CreateProvider(new FakeProcessRunner().Responding("env", string.Empty, exitCode: 1))
            .PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("Go did not say where", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("Go reports", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6. The module cache is <c>pkg\mod</c> inside the workspace, so what the command empties
    /// has the user's installed binaries and their own source as siblings.
    /// </summary>
    [Fact]
    public async Task AssertsTheWorkspaceAndTheInstalledBinariesSurvive()
    {
        Populate(ModuleCache);
        Populate(Path.Combine(GoPath, "bin"));
        Populate(Path.Combine(GoPath, "src"));

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in (string[])[GoPath, Path.Combine(GoPath, "bin"), Path.Combine(GoPath, "src")])
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheInstalledBinariesVanished()
    {
        Populate(ModuleCache);
        var installed = Populate(Path.Combine(GoPath, "bin"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad command §5.6 exists to catch.
        Directory.Delete(installed, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(installed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One invocation for all three answers, and only one across a planning pass. Asking three times
    /// would be three process launches for a fact that cannot change while the pass runs.
    /// </summary>
    [Fact]
    public async Task AsksGoWhereThingsAreOnceAndThenRemembers()
    {
        Populate(BuildCache);
        var runner = Reporting(BuildCache, ModuleCache, GoPath);
        var provider = CreateProvider(runner);

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        await provider.PlanAsync();

        Assert.Single(runner.Invocations);
        Assert.Equal("env GOCACHE GOMODCACHE GOPATH", runner.Invocations[0].Arguments);
    }

    /// <summary>
    /// <c>go env -w</c> writes the locations to a file Go reads on every run, so a rescan has to ask
    /// again rather than measure a directory Go has stopped using.
    /// </summary>
    [Fact]
    public async Task AsksAgainAfterAnInvalidation()
    {
        Populate(BuildCache);
        var runner = Reporting(BuildCache, ModuleCache, GoPath);
        var provider = CreateProvider(runner);

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task WarnsWhenAGoBuildIsRunning()
    {
        Populate(BuildCache);

        var provider = new GoCacheProvider(
            _environment, Reporting(BuildCache, ModuleCache, GoPath), new FakeProcessInspector("go"));

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// §7.1 over the workspace <c>go env</c> reports, which is the whole point of declaring it: the
    /// plan asserts the workspace, its <c>bin</c> and its <c>src</c> must survive, and those are the
    /// user's installed binaries and their own source.
    ///
    /// <para>The premise is asserted first. This fixture reports a workspace that is not the
    /// documented default, so nothing the synchronous declaration says reaches any of these paths —
    /// which is what made Explore allow all three.</para>
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheWorkspaceGoReportsAndWhatThePlanProtectsInside()
    {
        Populate(BuildCache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, [provider]);

        Assert.NotEqual(provider.DefaultGoPath, GoPath);
        Assert.All(
            new[] { GoPath, Path.Combine(GoPath, "bin"), Path.Combine(GoPath, "src") },
            path => Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

        Assert.False(policy.MayRemove(GoPath).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GoPath, "bin")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GoPath, "src")).IsAllowed);

        // §5.2's other direction, at the level below: the module cache is what 'go clean -modcache'
        // empties and stays removable, and anything else Go keeps beside it does not.
        Assert.True(policy.MayRemove(ModuleCache).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(GoPath, "pkg", "sumdb")).IsAllowed);

        // The plan protects those three and nothing else in the workspace.
        Assert.True(policy.MayRemove(Path.Combine(GoPath, "notes")).IsAllowed);
    }

    /// <summary>
    /// A <c>GOPATH</c> that lists two workspaces protects both, on both routes, and the module
    /// cache defaults to the first. <c>go env</c> prints the list as it was set, and read as one path
    /// it names no directory at all.
    /// </summary>
    [Fact]
    public async Task ProtectsEveryWorkspaceAGoPathListNames()
    {
        var first = Path.Combine(_environment.UserProfile, "go-first");
        var second = Path.Combine(_environment.UserProfile, "go-second");
        var firstModules = Populate(Path.Combine(first, "pkg", "mod"));
        Populate(BuildCache);

        // No GOMODCACHE, so the provider has to work out where the list puts it.
        var provider = CreateProvider(Reporting(BuildCache, string.Empty, $"{first}{Path.PathSeparator}{second}"));
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, [provider]);

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(firstModules));

        foreach (var workspace in new[] { first, second })
        {
            foreach (var path in new[] { workspace, Path.Combine(workspace, "bin"), Path.Combine(workspace, "src") })
            {
                Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                Assert.False(policy.MayRemove(path).IsAllowed, path);
            }
        }
    }

    /// <summary>
    /// <c>GOPATH</c> set to the profile itself makes the profile a workspace. Explore refuses the
    /// paths the plan protects there, and the rest of the profile stays ordinary.
    /// </summary>
    [Fact]
    public async Task AWorkspaceThatIsTheProfileLeavesTheRestOfTheProfileOrdinary()
    {
        var profile = _environment.UserProfile;
        var documents = Populate(Path.Combine(profile, "Documents"));
        Populate(BuildCache);

        var provider = CreateProvider(Reporting(BuildCache, ModuleCache, profile));
        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, [provider]);

        Assert.True(policy.MayRemove(documents).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(profile, "bin")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(profile, "src")).IsAllowed);
    }

    /// <summary>
    /// No subprocess where Go is not installed. The declaration is read when Explore builds its
    /// policy, which is a machine-wide question asked of every provider, so a provider that answered
    /// by running its tool anyway would start one console process per absent toolchain.
    /// </summary>
    [Fact]
    public async Task AsksGoNothingWhenGoIsNotInstalled()
    {
        var runner = new FakeProcessRunner();

        var roots = await new GoCacheProvider(
            new FakeUserEnvironment(_temp.Path), runner, FakeProcessInspector.NothingRunning)
            .DiscoverToolRootsAsync();

        Assert.Empty(roots);
        Assert.Empty(runner.Invocations);
    }
}
