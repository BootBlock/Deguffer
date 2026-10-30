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
    private const string FinishedBuild = "fmt0011223344556";
    private const string UnfinishedBuild = "fmt6655443322110";

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
        new(
            new ConanRcWalk(_temp.Path),
            _environment,
            runner,
            inspector ?? FakeProcessInspector.NothingRunning,
            system: new FakeSystemDirectories(Path.Combine(_temp.Path, "system")),
            volumes: new FakeVolumeInventory());

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

    private static void WriteConf(string home, string text, string file = "global.conf")
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, file), text);
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
        var provider = new ConanCacheProvider(new ConanRcWalk(_temp.Path), environment, runner, FakeProcessInspector.NothingRunning);

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

    /// <summary>
    /// The home and its configuration were judged from Deguffer's own reading, so a Conan that reports
    /// another home works somewhere nobody judged. Nothing is offered, and Conan is asked nothing more.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhereConanReportsAnotherHome()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var reported = Path.Combine(_temp.Path, "elsewhere", "conan-home");
        CreateHome(reported);
        CreateStore(Path.Combine(reported, "p"));
        var runner = Answering(reported);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"Conan reports its home as {reported}", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Invocations, call => call.Arguments.StartsWith("config show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesTheStoreTheSettingMovesItToWhereConanAgrees()
    {
        CreateHome(DefaultHome);
        var store = Path.Combine(_temp.Path, "storage");
        CreateStore(store);
        WriteConf(DefaultHome, $"# The shared drive is full.\ncore.cache:storage_path = {store}\n");

        // Coloured as CLICOLOR_FORCE makes it.
        var answer = $"\u001b[36mcore.cache:storage_path: \u001b[0m{store}\r\n";
        var plan = await CreateProvider(Answering(storeAnswer: answer)).PlanAsync();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Contains(Path.Combine(store, Recipe, "s"), command.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(store, ConanStoreContents.Database), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same for the store: a Conan that uses another one than its configuration names is not believed.</summary>
    [Fact]
    public async Task OffersNothingWhereConanReportsAnotherStoreThanItsConfigurationNames()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var reported = Path.Combine(_temp.Path, "storage");
        CreateStore(reported);

        var plan = await CreateProvider(Answering(storeAnswer: $"core.cache:storage_path: {reported}\r\n")).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"Conan reports its package store as {reported}", StringComparison.Ordinal));
    }

    /// <summary>
    /// A configuration Deguffer cannot read plainly names a store only Conan can say, and asking
    /// Conan would make that store before it could be judged. Conan is not started.
    /// </summary>
    [Theory]
    [InlineData("global.conf", "core.cache:storage_path = {{ os.getenv('USERPROFILE') }}\n", "is a template")]
    [InlineData("global.conf", "{% include 'storage.conf' %}\n", "is a template")]
    [InlineData("global_user.conf", "core.cache:storage_path+=C:\\Conan\n", "Line 1 of")]
    [InlineData("global.conf", "core:non_interactive = True\ncore.cache:storage_path = storage\n", "Line 2 of")]
    public async Task NeverStartsConanWhereItsConfigurationIsNotPlain(string file, string text, string why)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        WriteConf(DefaultHome, text, file);
        var runner = Answering();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains(why, StringComparison.Ordinal) && note.Message.Contains("Conan was not started", StringComparison.Ordinal));
        Assert.Empty(runner.Invocations);
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

    /// <summary>
    /// The home is worked out without Conan once per pass, so the plan, the cleaned places and the
    /// question to Conan all go by one reading of the <c>.conanrc</c> and <c>CONAN_HOME</c>, and the
    /// next pass reads them again.
    /// </summary>
    [Fact]
    public async Task WorksOutTheHomeOncePerPass()
    {
        var first = Path.Combine(_temp.Path, "first-home");
        var second = Path.Combine(_temp.Path, "second-home");
        var inSecond = Path.Combine(second, "p", Recipe, "s", "zlib.c");
        var provider = CreateProvider(new FakeProcessRunner());

        _environment.WithEnvironmentVariable("CONAN_HOME", first);
        Assert.Contains(await provider.CleanedPlacesAsync(), place => place.Holds(Path.Combine(first, "p", Recipe, "s", "zlib.c")));

        _environment.WithEnvironmentVariable("CONAN_HOME", second);
        Assert.DoesNotContain(await provider.CleanedPlacesAsync(), place => place.Holds(inSecond));

        provider.InvalidateCaches();
        Assert.Contains(await provider.CleanedPlacesAsync(), place => place.Holds(inSecond));
    }

    /// <summary>
    /// A build started while the preview is on screen is an unfinished build that <c>-t</c> takes, so
    /// the clean asks again immediately before the command and does not run it.
    /// </summary>
    [Fact]
    public async Task DoesNotRunTheCleanOnceConanStartsAfterThePreview()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var runner = Answering();
        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(runner, inspector);

        var plan = await provider.PlanAsync();
        Assert.Single(plan.Steps);

        inspector.WithRunning("conan");
        var result = await provider.ExecuteAsync(plan);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("conan is running now", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, call => call.Arguments.StartsWith("cache clean", StringComparison.Ordinal));
        Assert.Contains(IUseCheck.ReadingLimit, inspector.AgeLimits);
    }

    /// <summary>
    /// §5.2: <c>-t</c> removes <c>t</c> whole and every folder of <c>b</c> without a finished package,
    /// whatever it is called. Anything there Conan did not make holds the clean back, and is protected.
    /// </summary>
    [Theory]
    [InlineData("b", "notes")]
    [InlineData("b", "fmt0011223344556x")]
    [InlineData("t", "notes")]
    public async Task WithholdsTheCleanWhereItWouldTakeAFolderConanDidNotMake(string parent, string name)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var foreign = Path.Combine(Store, parent, name);
        Write(10, foreign, "readme.txt");

        var plan = await CreateProvider(Answering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"'{Path.Combine(parent, name)}'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(foreign, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Store, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2: a recipe folder that is a link would have <c>-s</c> empty the <c>s</c> in whatever it
    /// points at, a tree nobody read, so it holds the clean back and is protected, as a linked
    /// <c>t</c> or <c>b</c> does.
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task WithholdsTheCleanWhereARecipeFolderIsALink(DirectoryLinkKind kind)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var target = Path.Combine(_temp.Path, "elsewhere", "project");
        var unread = Write(10, target, "s", "main.c");
        var link = Path.Combine(Store, "zlib0123456789abd");
        DirectoryLink.Create(kind, link, target);

        var plan = await CreateProvider(Answering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains("'zlib0123456789abd'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(unread));
    }

    /// <summary>A file in <c>t</c> goes with it, so it holds the clean back as a foreign folder does.</summary>
    [Fact]
    public async Task WithholdsTheCleanWhereTheTemporaryFolderHoldsAFile()
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var file = Write(10, Store, "t", "notes.txt");

        var plan = await CreateProvider(Answering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(file, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    public enum HomeRoute
    {
        ConanHomeVariable,
        ConanRc,
        ConansAnswer,
    }

    /// <summary>
    /// A home a setting puts at the profile is not Conan's to declare whole. Conan's clean is not run
    /// there, and only Conan's own entries are refused in Explore.
    /// </summary>
    [Theory]
    [InlineData(HomeRoute.ConanHomeVariable)]
    [InlineData(HomeRoute.ConanRc)]
    [InlineData(HomeRoute.ConansAnswer)]
    public async Task DeclinesAHomeSetToTheProfile(HomeRoute route)
    {
        var profile = _environment.UserProfile;
        CreateHome(DefaultHome);
        CreateStore(Store);
        CreateStore(Path.Combine(profile, "p"));

        switch (route)
        {
            case HomeRoute.ConanHomeVariable:
                _environment.WithEnvironmentVariable("CONAN_HOME", profile);
                break;

            case HomeRoute.ConanRc:
                File.WriteAllText(Path.Combine(_environment.LocalAppData, ".conanrc"), $"conan_home={profile}");
                break;
        }

        var runner = Answering(profile);
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        var root = Assert.Single(await provider.DiscoverToolRootsAsync());

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, note => note.Message.Contains("will not run Conan's clean", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Invocations, call => call.Arguments.StartsWith("config show", StringComparison.Ordinal));
        Assert.Equal(route is HomeRoute.ConansAnswer, runner.Invocations.Count > 0);
        Assert.Equal(profile, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild("global.conf", ChildKind.File)));
        Assert.False(root.Recognises(new ToolRootChild("p", ChildKind.Folder)));
        Assert.True(root.Recognises(new ToolRootChild("Documents", ChildKind.Folder)));
        Assert.DoesNotContain(await provider.CleanedPlacesAsync(), place => place.Holds(Path.Combine(profile, "p", Recipe, "s", "zlib.c")));
    }

    /// <summary>
    /// The same for the store: <c>core.cache:storage_path</c> at the profile or a drive's root is never
    /// walked as a store, and only the database is refused there. Where the configuration says so,
    /// Conan is never started, because its first command would make a database in that folder.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task DeclinesAStoreSetToTheProfileOrADriveRoot(bool driveRoot, bool configured)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var store = driveRoot ? Path.GetPathRoot(_temp.Path)! : _environment.UserProfile;

        if (configured)
        {
            WriteConf(DefaultHome, $"core.cache:storage_path={store}\n");
        }

        var runner = Answering(storeAnswer: $"core.cache:storage_path: {store}\r\n");
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.Equal(configured, runner.Invocations.Count == 0);
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.ProtectedPaths);
        Assert.Contains(plan.Notes, note => note.Message.Contains("will not run Conan's clean", StringComparison.Ordinal));
        var root = Assert.Single(roots, root => root.Path.Equals(store, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ToolRootClaim.NamedEntries, root.Claim);
        Assert.False(root.Recognises(new ToolRootChild(ConanStoreContents.Database, ChildKind.File)));
        Assert.Contains(roots, root => root.Path.Equals(DefaultHome, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6, the negative: a clean that also takes something the plan names as a survivor fails
    /// verification.
    /// </summary>
    [Theory]
    [InlineData("package")]
    [InlineData("export")]
    [InlineData("metadata")]
    [InlineData("built")]
    [InlineData("database")]
    public async Task VerificationFailsWhereTheCleanTookASurvivor(string survivor)
    {
        CreateHome(DefaultHome);
        CreateStore(Store);
        var taken = survivor switch
        {
            "package" => Path.Combine(Store, Package, "p"),
            "export" => Path.Combine(Store, Recipe, "e"),
            "metadata" => Path.Combine(Store, Recipe, "d"),
            "built" => Path.Combine(Store, "b", FinishedBuild, "p"),
            _ => Path.Combine(Store, ConanStoreContents.Database),
        };

        var runner = Answering().Replying(Conan, arguments =>
        {
            if (arguments != "cache clean -s -b -t")
            {
                return null;
            }

            Directory.Delete(Path.Combine(Store, Recipe, "s"), recursive: true);
            Directory.Delete(Path.Combine(Store, "t"), recursive: true);

            if (File.Exists(taken))
            {
                File.Delete(taken);
            }
            else
            {
                Directory.Delete(taken, recursive: true);
            }

            return new CommandOutcome(0, string.Empty, string.Empty);
        });
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.False((await provider.VerifyAsync(plan)).Passed);
    }
}
