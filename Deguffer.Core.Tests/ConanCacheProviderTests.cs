using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1 with a pinned scope, and the rule that sets this provider apart: Conan creates its home when
/// asked anything, so Conan is started only once the home it would use is known to be there.
/// </summary>
public sealed class ConanCacheProviderTests : IDisposable
{
    private const string Recipe = "zlib0123456789abc";
    private const string Package = "zlibfedcba9876543";
    private const string FinishedBuild = "fmt__00112233445566";
    private const string UnfinishedBuild = "fmt__66554433221100";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ConanCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("conan");

    public void Dispose() => _temp.Dispose();

    private string Conan => _environment.FindExecutable("conan")!;

    private string DefaultHome => Path.Combine(_environment.UserProfile, ".conan2");

    private string Store => Path.Combine(DefaultHome, "p");

    /// <summary>Conan answering for the default home, with its store in the default place.</summary>
    private FakeProcessRunner Answering(string? home = null, string storeAnswer = "") =>
        new FakeProcessRunner()
            .Responding(Conan, "config home", (home ?? DefaultHome) + "\r\n")
            .Responding(Conan, "config show core.cache:storage_path", storeAnswer);

    private ConanCacheProvider CreateProvider(FakeProcessRunner runner, FakeProcessInspector? inspector = null) =>
        new(_environment, runner, inspector ?? FakeProcessInspector.NothingRunning);

    private static string Write(int bytes, params string[] segments)
    {
        var path = Path.Combine(segments);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// A store holding one of each thing Conan lays out: a recipe, a downloaded package, a package
    /// built here, a build that never finished, the temporary folder, the database and a folder Conan
    /// did not make.
    /// </summary>
    private void CreateStore(string store)
    {
        Write(100, store, ConanStoreContents.Database);
        Write(200, store, Recipe, "e", "conanfile.py");
        Write(300, store, Recipe, "es", "patch.diff");
        Write(1000, store, Recipe, "s", "zlib.c");
        Write(400, store, Recipe, "d", "metadata", "sbom.json");
        Write(500, store, Package, "p", "lib", "zlib.lib");
        Write(600, store, Package, "d", "conan_package.tgz");
        Write(2000, store, "b", FinishedBuild, "b", "fmt.obj");
        Write(700, store, "b", FinishedBuild, "p", "conanmanifest.txt");
        Write(800, store, "b", FinishedBuild, "p", "conaninfo.txt");
        Write(900, store, "b", FinishedBuild, "d", "metadata", "logs.txt");
        Write(4000, store, "b", UnfinishedBuild, "b", "fmt.obj");
        Write(50, store, "b", UnfinishedBuild, "p", "conanmanifest.txt");
        Write(8000, store, "t", "zlib0123456789abc", "e", "conanfile.py");
        Write(16000, store, "notes", "s", "readme.txt");
    }

    private void CreateHome(string home)
    {
        Write(10, home, "global.conf");
        Write(10, home, "remotes.json");
        Write(10, home, "profiles", "default");
        Write(10, home, "extensions", "hooks", "hook_check.py");
        Write(10, home, "sources", "0123abcd");
    }

    [Fact]
    public async Task ReportsNotPresentAndAsksNothingWhenConanIsNotInstalled()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var runner = new FakeProcessRunner();
        var provider = new ConanCacheProvider(environment, runner, FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>
    /// Every Conan command creates a missing home, so a machine where Conan was never used must not
    /// be handed one by a scan.
    /// </summary>
    [Fact]
    public async Task NeverStartsConanWhereItsHomeDoesNotExist()
    {
        var runner = Answering();
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        await provider.DiscoverToolRootsAsync();
        await provider.CleanedPlacesAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.HasUnreadableRoot);
        Assert.False(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);
        Assert.False(Directory.Exists(DefaultHome));
    }

    [Fact]
    public async Task ReadsAnUnreadableHomeAsUnreachedRatherThanAbsent()
    {
        Directory.CreateDirectory(DefaultHome);
        var runner = Answering();

        CleanupPlan plan;

        using (DeniedDirectory.WithUnreadableAttributes(DefaultHome))
        {
            plan = await CreateProvider(runner).PlanAsync();
        }

        Assert.True(plan.HasUnreadableRoot);
        Assert.Empty(plan.Steps);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ReadsAStoreThatWillNotBeListedAsUnreached()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        CleanupPlan plan;

        using (new DeniedDirectory(Store))
        {
            plan = await CreateProvider(Answering()).PlanAsync();
        }

        Assert.True(plan.HasUnreadableRoot);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public async Task PlansConansOwnCleanWithItsScopePinned()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var plan = await CreateProvider(Answering()).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Conan, command.FileName);
        Assert.Equal("cache clean -s -b -t", command.Arguments);
        Assert.Equal(CancelledCommand.Finishes, command.WhenCancelled);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"Conan reports its home as {DefaultHome}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sources, the build folders, the build that never finished and the temporary folder, and
    /// nothing beside them: not a finished local build's package, and not a folder named <c>s</c>
    /// inside something Conan did not make.
    /// </summary>
    [Fact]
    public async Task MeasuresOnlyWhatTheCleanRemoves()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var plan = await CreateProvider(Answering()).PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal(
            new[]
            {
                Path.Combine(Store, Recipe, "s"),
                Path.Combine(Store, "b", FinishedBuild, "b"),
                Path.Combine(Store, "b", UnfinishedBuild),
                Path.Combine(Store, "t"),
            }.Order(StringComparer.OrdinalIgnoreCase),
            command.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1000 + 2000 + 4000 + 50 + 8000, command.EstimatedBytes);
    }

    /// <summary>§5.6: the configuration, the store, its database, and everything in it the clean leaves.</summary>
    [Fact]
    public async Task ProtectsEverythingTheCleanLeaves()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var plan = await CreateProvider(Answering()).PlanAsync();

        string[] survivors =
        [
            DefaultHome,
            Path.Combine(DefaultHome, "global.conf"),
            Path.Combine(DefaultHome, "remotes.json"),
            Path.Combine(DefaultHome, "profiles"),
            Path.Combine(DefaultHome, "extensions"),
            Path.Combine(DefaultHome, "sources"),
            Store,
            Path.Combine(Store, ConanStoreContents.Database),
            Path.Combine(Store, Recipe),
            Path.Combine(Store, Recipe, "e"),
            Path.Combine(Store, Recipe, "es"),
            Path.Combine(Store, Recipe, "d"),
            Path.Combine(Store, Package, "p"),
            Path.Combine(Store, Package, "d"),
            Path.Combine(Store, "b", FinishedBuild, "p"),
            Path.Combine(Store, "b", FinishedBuild, "d"),
            Path.Combine(Store, "notes"),
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.DoesNotContain(plan.ProtectedPaths, p => command.MeasuredPaths.Contains(p.Path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OffersNothingForAStoreHoldingOnlyWhatItDoesNotRecognise()
    {
        CreateHome(DefaultHome);
        Write(100, Store, ConanStoreContents.Database);
        Write(16000, Store, "notes", "s", "readme.txt");
        Write(16000, Store, "zlib0123456789ABC", "s", "zlib.c");

        var plan = await CreateProvider(Answering()).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Steps);
    }

    /// <summary>§5.6 run end to end: the clean as Conan performs it, and every survivor still there.</summary>
    [Fact]
    public async Task VerifiesTheSurvivorsAfterTheClean()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var runner = Answering().Replying(Conan, arguments =>
        {
            if (arguments != "cache clean -s -b -t")
            {
                return null;
            }

            Directory.Delete(Path.Combine(Store, Recipe, "s"), recursive: true);
            Directory.Delete(Path.Combine(Store, "b", FinishedBuild, "b"), recursive: true);
            Directory.Delete(Path.Combine(Store, "b", UnfinishedBuild), recursive: true);
            Directory.Delete(Path.Combine(Store, "t"), recursive: true);
            return new CommandOutcome(0, string.Empty, string.Empty);
        });
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.Equal(1000 + 2000 + 4000 + 50 + 8000, result.BytesReclaimed);
        Assert.True(verification.Passed);
    }

    [Fact]
    public async Task HoldsTheCleanBackWhileConanRuns()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var runner = Answering();

        var plan = await CreateProvider(runner, new FakeProcessInspector("conan")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Severity is PlanNoteSeverity.Warning && note.Message.Contains("conan is running", StringComparison.Ordinal));
        Assert.Empty(runner.Invocations);
    }

    [Theory]
    [InlineData("conan2")]
    [InlineData(@"\Users\testuser\.conan2")]
    [InlineData("")]
    public async Task OffersNothingWhereConansHomeIsNotAFullPath(string reported)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var runner = new FakeProcessRunner().Responding(Conan, "config home", reported + "\r\n");

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.DoesNotContain(runner.Invocations, call => call.Arguments.StartsWith("config show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesTheHomeConanReportsOverItsOwnReading()
    {
        Directory.CreateDirectory(DefaultHome);
        var reported = Path.Combine(_temp.Path, "elsewhere", "conan-home");
        CreateHome(reported);
        CreateStore(Path.Combine(reported, "p"));

        var plan = await CreateProvider(Answering(reported)).PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Contains(Path.Combine(reported, "p", Recipe, "s"), command.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(reported, "global.conf"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UsesTheStoreConanReportsWhereTheSettingMovesIt()
    {
        CreateHome(DefaultHome);
        var store = Path.Combine(_temp.Path, "storage");
        CreateStore(store);

        // Coloured as CLICOLOR_FORCE makes it.
        var answer = $"\u001b[36mcore.cache:storage_path: \u001b[0m{store}\r\n";
        var plan = await CreateProvider(Answering(storeAnswer: answer)).PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Contains(Path.Combine(store, Recipe, "s"), command.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(store, ConanStoreContents.Database), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("core.cache:storage_path: storage\r\n")]
    [InlineData("something else\r\n")]
    public async Task OffersNothingWhereTheStoreCannotBeRead(string answer)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var plan = await CreateProvider(Answering(storeAnswer: answer)).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task ReadsAnUnsetStorageSettingAsTheDefaultStore()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);

        var plan = await CreateProvider(Answering(storeAnswer: "core.cache:storage_path: None\r\n")).PlanAsync();

        Assert.Contains(Path.Combine(Store, Recipe, "s"), Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps)).MeasuredPaths);
    }

    [Fact]
    public async Task StartsConanForAHomeNamedByConanHome()
    {
        var home = Path.Combine(_temp.Path, "conan-home");
        CreateHome(home);
        _environment.WithEnvironmentVariable("CONAN_HOME", home);
        var runner = Answering(home);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, call => call.Arguments == "config home");
        Assert.False(plan.WasNotExamined);
    }

    [Fact]
    public async Task NeverStartsConanWhereConanWouldStopOnItsConanRc()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        File.WriteAllText(Path.Combine(_environment.LocalAppData, ".conanrc"), "conan_home\n");
        var runner = Answering();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void ExploreRefusesTheHomeAndEverythingInIt()
    {
        var provider = CreateProvider(new FakeProcessRunner());
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(DefaultHome).IsAllowed);
        Assert.False(policy.MayRemove(Store).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(DefaultHome, "profiles")).IsAllowed);
    }

    [Fact]
    public async Task NamesEverySourceAndBuildFolderInTheStoreAsCleaned()
    {
        var places = await CreateProvider(new FakeProcessRunner()).CleanedPlacesAsync();

        Assert.Contains(places, place => place.Holds(Path.Combine(Store, Recipe, "s", "zlib.c")));
        Assert.Contains(places, place => place.Holds(Path.Combine(Store, "b", UnfinishedBuild, "p", "conanmanifest.txt")));
        Assert.Contains(places, place => place.Holds(Path.Combine(Store, "t", Recipe, "e", "conanfile.py")));
        Assert.DoesNotContain(places, place => place.Holds(Path.Combine(Store, Recipe, "e", "conanfile.py")));
    }
}
