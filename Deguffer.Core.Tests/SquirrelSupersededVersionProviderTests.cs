using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider that removes an installed program rather than something a program wrote, so
/// the assertions are about what it refuses far more than about what it takes.
///
/// <para>Four things have to hold. The build in use is never a target, however many are on disk. An
/// installation holding a version number Deguffer cannot order gives up nothing at all, because
/// "which one is current?" then has no answer. An application that is running is refused outright
/// rather than warned about. And a folder that fails either half of the identification test is
/// never reached into.</para>
/// </summary>
public sealed class SquirrelSupersededVersionProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SquirrelSupersededVersionProviderTests() =>
        _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private SquirrelSupersededVersionProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(
            _environment,
            liveTrees: liveTrees ?? FakeLiveTreeInspector.NothingLive,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning);

    /// <summary>A directory with a file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    /// <summary>
    /// An application Squirrel installed: the updater beside every one it manages, a directory per
    /// build, and the packages folder it updates from.
    /// </summary>
    private string CreateApplication(string name, params string[] versions)
    {
        var root = Path.Combine(_environment.LocalAppData, name);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        Populate(Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName));

        foreach (var version in versions)
        {
            Populate(Path.Combine(root, "app-" + version));
        }

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSquirrelApplication()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Three builds on disk, and the newest is the one the application launches. Ordering is by
    /// version rather than by name or by date, which is the rule the application's own shim uses.
    /// </summary>
    [Fact]
    public async Task PlansEveryBuildExceptTheNewest()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4", "3.10.0");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Path.Combine(root, "app-3.6.3"), Path.Combine(root, "app-3.6.4") }
                .Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
    }

    /// <summary>An application holding one build has nothing to give up, and says so plainly.</summary>
    [Fact]
    public async Task AnApplicationHoldingOneBuildOffersNothing()
    {
        CreateApplication("Chatterbox", "3.6.4");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("one build and no more", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6's negative, and the one that matters most here: the build in use, the updater, the
    /// packages folder and the application's own folder all survive a run that removed the build it
    /// replaced.
    /// </summary>
    [Fact]
    public async Task TheBuildInUseTheUpdaterAndThePackagesAllSurvive()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        string[] directories =
        [
            root,
            Path.Combine(root, "app-3.6.4"),
            Path.Combine(root, SquirrelDiscovery.PackagesDirectoryName),
        ];

        var updater = Path.Combine(root, SquirrelDiscovery.UpdaterName);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(root, "app-3.6.3"), Assert.Single(plan.TargetedPaths));

        foreach (var path in directories.Append(updater))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(directories, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.True(File.Exists(updater), $"{updater} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.False(Directory.Exists(Path.Combine(root, "app-3.6.3")));
    }

    /// <summary>
    /// G8's unrecognised case, and here it decides which build is the running one. A pre-release
    /// version orders below its own release under one reading and above it under another, so an
    /// installation holding one gives up nothing — the alternative is to name the build in use as
    /// superseded and remove the application out from under the user.
    /// </summary>
    [Theory]
    [InlineData("3.7.0-beta1")]
    [InlineData("preview")]
    public async Task AVersionNumberItCannotOrderLeavesEveryBuildAlone(string unreadable)
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var odd = Populate(Path.Combine(root, "app-" + unreadable));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == odd && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(root, "app-3.6.3")), "an older build was removed");
    }

    /// <summary>
    /// §5.3, and here it is a refusal rather than a warning. The process holding the application
    /// open runs from the build it did <em>not</em> supersede, so the question this answers is
    /// whether the application is running at all — not whether the old directory itself is busy.
    /// </summary>
    [Fact]
    public async Task AnApplicationThatIsRunningGivesUpNothing()
    {
        var running = CreateApplication("Chatterbox", "3.6.3", "3.6.4");
        var idle = CreateApplication("Notepad", "1.0", "1.1");

        var provider = CreateProvider(new FakeLiveTreeInspector(running));
        var plan = await provider.PlanAsync();

        Assert.Equal(Path.Combine(idle, "app-1.0"), Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.Notes, n => n.Message.Contains("Chatterbox", StringComparison.Ordinal));
        Assert.Contains(
            plan.ProtectedPaths,
            p => p.Path == Path.Combine(running, "app-3.6.3") && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(
            Directory.Exists(Path.Combine(running, "app-3.6.3")),
            "a build was removed while the application was running");
    }

    /// <summary>
    /// A check that could not run must not look like a check that found nothing (§5.5's reasoning,
    /// applied to a safeguard rather than a measurement).
    /// </summary>
    [Fact]
    public async Task AMachineWhereLivenessCannotBeEstablishedSaysSo()
    {
        CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        var plan = await CreateProvider(FakeLiveTreeInspector.CannotTell).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check", StringComparison.Ordinal));
    }

    /// <summary>
    /// Identification is the updater <em>and</em> a build directory whose version can be read. A
    /// folder with only one of them is not established as a Squirrel application, so nothing in it
    /// is ever offered.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AFolderMissingHalfTheIdentificationIsNeverReachedInto(bool updater, bool builds)
    {
        var root = Path.Combine(_environment.LocalAppData, "NotSquirrel");
        Directory.CreateDirectory(root);

        if (updater)
        {
            File.WriteAllBytes(Path.Combine(root, SquirrelDiscovery.UpdaterName), new byte[64]);
        }

        if (builds)
        {
            Populate(Path.Combine(root, "app-1.0.0"));
            Populate(Path.Combine(root, "app-1.1.0"));
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(provider.ToolRoots);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);

        if (builds)
        {
            Assert.True(Directory.Exists(Path.Combine(root, "app-1.0.0")), "a build was removed");
        }
    }

    /// <summary>
    /// §5.2 as §7.1 reads it. Explore refuses the application's folder, its updater and the build in
    /// use, and allows only a build the application has replaced — which is the one thing the
    /// Storage page offers here.
    /// </summary>
    [Fact]
    public void TheDeclarationRefusesEverythingExceptASupersededBuild()
    {
        var root = CreateApplication("Chatterbox", "3.6.3", "3.6.4");

        var declaration = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(root, declaration.Path);
        Assert.True(declaration.Recognises("app-3.6.3"));
        Assert.False(declaration.Recognises("app-3.6.4"));
        Assert.False(declaration.Recognises(SquirrelDiscovery.UpdaterName));
        Assert.False(declaration.Recognises(SquirrelDiscovery.PackagesDirectoryName));
        Assert.False(declaration.Recognises("app.ico"));
    }

    /// <summary>
    /// G4: the profile is swept once for the life of a planning pass, and again after an
    /// invalidation, so an application updated while Deguffer was open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheProfileIsSweptOncePerPassAndAgainAfterInvalidation()
    {
        CreateApplication("Chatterbox", "3.6.4");

        var provider = CreateProvider();

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        var older = Populate(Path.Combine(_environment.LocalAppData, "Chatterbox", "app-3.6.3"));

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal(older, Assert.Single((await provider.PlanAsync()).TargetedPaths));
    }
}
