using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// uv's answers about where it keeps its cache, tools and interpreters are remembered across passes
/// and Explore builds while everything uv read to give them is unchanged, and asked again the moment
/// any of it changes (#1059). The plan checks the cache against uv's own folders before it sends
/// <c>uv cache clean</c>, which removes the whole cache folder, so it asks uv afresh whatever the memory
/// holds.
///
/// <para>Every provider here shares one <see cref="ToolAnswerMemory"/> whose clock runs a minute ahead
/// of the files the fixture writes, as a real ask starts long after a configuration file was saved.
/// Each "asks again" case starts from trusted answers (<see cref="TrustAsync"/>), so only the change it
/// names can explain the ask.</para>
/// </summary>
public sealed class UvLocationMemoryTests : IDisposable
{
    private const string CacheDir = "cache dir";
    private const string ToolDir = "tool dir";
    private const string PythonDir = "python dir";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner = new();
    private readonly string _uv;
    private int _since;

    public UvLocationMemoryTests()
    {
        _uv = Path.Combine(_temp.CreateDirectory("bin"), "uv.exe");
        File.WriteAllText(_uv, "uv 0.11");

        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("uv", _uv);
        _memory = new ToolAnswerMemory(_clock);

        CreateCache(DefaultCache);
        Directory.CreateDirectory(Tools);
        Directory.CreateDirectory(Python);
        Answering(DefaultCache);
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultCache => Path.Combine(_environment.LocalAppData, "uv", "cache");

    private string MovedCache => Path.Combine(_temp.Path, "uv-cache");

    private string Tools => Path.Combine(_environment.RoamingAppData, "uv", "tools");

    private string Python => Path.Combine(_environment.RoamingAppData, "uv", "python");

    private string UserConfig => Path.Combine(_environment.RoamingAppData, "uv", "uv.toml");

    /// <summary>How often uv was asked <paramref name="query"/> since its answers were last trusted.</summary>
    private int Asked(string query) =>
        _runner.Invocations.Skip(_since).Count(invocation => invocation.Arguments.StartsWith(query, StringComparison.Ordinal));

    private string Key(string query) => $"{query} --color never|{_uv}";

    /// <summary>uv's answers from now on: its cache at <paramref name="cache"/>, its tools and interpreters where they are.</summary>
    private void Answering(string cache) =>
        _runner
            .Responding(_uv, CacheDir, cache)
            .Responding(_uv, ToolDir, Tools)
            .Responding(_uv, PythonDir, Python);

    /// <summary>
    /// Every write happens before the first ask unless the test makes it after, so the clock is moved
    /// a minute past the moment it was called each time a provider is built.
    /// </summary>
    private UvCacheProvider CreateProvider()
    {
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));

        return new UvCacheProvider(_environment, _runner, FakeProcessInspector.NothingRunning, memory: _memory);
    }

    private static void CreateCache(string root) => Write(Path.Combine(root, "wheels-v5", "package.whl"), new string('x', 4096));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Asks for both declarations Explore and a duplicate search read, which read the memory.</summary>
    private static async Task<IReadOnlyList<CleanedPlace>> DeclaredAsync(UvCacheProvider provider)
    {
        await provider.DiscoverToolRootsAsync();
        return await provider.CleanedPlacesAsync();
    }

    /// <summary>
    /// Passes until uv's three answers are trusted: the first holds them, the second confirms them, and
    /// a third with nothing changed must ask nothing. The counts then start again.
    /// </summary>
    private async Task TrustAsync(UvCacheProvider provider)
    {
        await DeclaredAsync(provider);
        provider.InvalidateCaches();
        await DeclaredAsync(provider);
        Assert.True(_memory.Holds(Key(CacheDir)));
        Assert.True(_memory.Holds(Key(ToolDir)));
        Assert.True(_memory.Holds(Key(PythonDir)));

        provider.InvalidateCaches();
        await DeclaredAsync(provider);
        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(2, Asked(ToolDir));
        Assert.Equal(2, Asked(PythonDir));

        _since = _runner.Invocations.Count;
    }

    [Fact]
    public async Task APassOnceTheAnswersAreConfirmedWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();

        await DeclaredAsync(provider);
        Assert.False(_memory.Holds(Key(CacheDir)));
        provider.InvalidateCaches();
        await DeclaredAsync(provider);
        Assert.True(_memory.Holds(Key(CacheDir)));
        provider.InvalidateCaches();
        var places = await DeclaredAsync(provider);

        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(2, Asked(ToolDir));
        Assert.Equal(2, Asked(PythonDir));
        Assert.Contains(places, place => place.Path.Equals(DefaultCache, StringComparison.OrdinalIgnoreCase));
    }

    public enum Change
    {
        Create,
        Write,
        Delete,
    }

    /// <summary>
    /// Every file uv reads to say where it keeps things, relative to the temp tree: its user
    /// configuration, and both project files in the folder it runs in and every one above it (sampled
    /// here at the working folder itself, the profile and the temp root).
    /// </summary>
    public static TheoryData<string, Change> DeclaredInputs()
    {
        string[] inputs =
        [
            @"profile\AppData\Roaming\uv\uv.toml",
            @"profile\AppData\Local\Deguffer\tool-working-directory\uv.toml",
            @"profile\AppData\Local\Deguffer\tool-working-directory\pyproject.toml",
            @"profile\uv.toml",
            @"profile\pyproject.toml",
            "uv.toml",
            "pyproject.toml",
        ];

        var data = new TheoryData<string, Change>();

        foreach (var input in inputs)
        {
            foreach (var change in Enum.GetValues<Change>())
            {
                data.Add(input, change);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input, Change change)
    {
        await AssertChangingAsksAgainAsync(Path.Combine(_temp.Path, input), change);
    }

    /// <summary>
    /// uv reads its system configuration from the drive <c>SYSTEMDRIVE</c> names, as it is started
    /// with it.
    /// </summary>
    [Theory]
    [InlineData(Change.Create)]
    [InlineData(Change.Write)]
    [InlineData(Change.Delete)]
    public async Task ChangingTheSystemConfigurationOnTheSystemDriveAsksAgain(Change change)
    {
        var drive = _temp.CreateDirectory("system-drive");
        _environment.WithToolEnvironmentVariable("SYSTEMDRIVE", drive);

        await AssertChangingAsksAgainAsync(Path.Combine(drive, "ProgramData", "uv", "uv.toml"), change);
    }

    /// <summary><c>UV_CONFIG_FILE</c> names a file of its own, taken from the folder uv runs in where it is relative.</summary>
    [Theory]
    [InlineData("absolute")]
    [InlineData("relative")]
    public async Task ChangingTheFileUvConfigFileNamesAsksAgain(string form)
    {
        var file = form == "absolute"
            ? Path.Combine(_temp.Path, "named", "uv.toml")
            : Path.Combine(ToolInputs.WorkingDirectory(_environment), "named", "uv.toml");
        _environment.WithToolEnvironmentVariable("UV_CONFIG_FILE", form == "absolute" ? file : @"named\uv.toml");

        await AssertChangingAsksAgainAsync(file, Change.Write);
    }

    /// <summary>
    /// uv reads its user configuration from the <c>APPDATA</c> it is started with, which may name
    /// another folder than the account's own.
    /// </summary>
    [Fact]
    public async Task ChangingTheUserConfigurationInTheRoamingFolderAppDataNamesAsksAgain()
    {
        var roaming = _temp.CreateDirectory("roaming-elsewhere");
        _environment.WithToolEnvironmentVariable("APPDATA", roaming);

        await AssertChangingAsksAgainAsync(Path.Combine(roaming, "uv", "uv.toml"), Change.Create);
    }

    /// <summary>
    /// uv keeps its tools and interpreters in <c>%APPDATA%\uv\data</c> wherever that folder exists, so
    /// creating or removing it moves both answers.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreatingOrRemovingTheLegacyStateFolderAsksAgain(bool exists)
    {
        var legacy = Path.Combine(_environment.RoamingAppData, "uv", "data");

        if (!exists)
        {
            Directory.CreateDirectory(legacy);
        }

        var provider = CreateProvider();
        await TrustAsync(provider);

        if (exists)
        {
            Directory.CreateDirectory(legacy);
        }
        else
        {
            Directory.Delete(legacy);
        }

        provider.InvalidateCaches();
        await DeclaredAsync(provider);

        Assert.Equal(1, Asked(ToolDir));
        Assert.Equal(1, Asked(PythonDir));
    }

    private async Task AssertChangingAsksAgainAsync(string path, Change change)
    {
        if (change is not Change.Create)
        {
            Write(path, "native-tls = true");
        }

        var provider = CreateProvider();
        await TrustAsync(provider);

        switch (change)
        {
            case Change.Create:
            case Change.Write:
                Write(path, "native-tls = true\ncache-dir = \"elsewhere\"");
                break;
            case Change.Delete:
                File.Delete(path);
                break;
        }

        provider.InvalidateCaches();
        await DeclaredAsync(provider);

        Assert.Equal(1, Asked(CacheDir));
        Assert.Equal(1, Asked(ToolDir));
        Assert.Equal(1, Asked(PythonDir));
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        CreateCache(MovedCache);
        var provider = CreateProvider();
        await TrustAsync(provider);

        _environment.WithToolEnvironmentVariable("UV_CACHE_DIR", MovedCache);
        Answering(MovedCache);
        provider.InvalidateCaches();
        var places = await DeclaredAsync(provider);

        Assert.Equal(1, Asked(CacheDir));
        Assert.Contains(places, place => place.Path.Equals(MovedCache, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheExecutableReplacedAsksAgain()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);

        File.WriteAllText(_uv, "uv 0.12, a newer build");
        provider.InvalidateCaches();
        await DeclaredAsync(provider);

        Assert.Equal(1, Asked(CacheDir));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The first build holds uv's answers and
    /// the second confirms them; a third is answered from the memory they filled, and still refuses
    /// the tools and interpreters uv reported where a variable moved them.
    /// </summary>
    [Fact]
    public async Task AnExploreBuildOnceTheAnswersAreConfirmedAnswersFromMemoryWithoutAskingUv()
    {
        var movedTools = _temp.CreateDirectory("uv-tools", "ruff");
        _runner.Responding(_uv, ToolDir, Path.GetDirectoryName(movedTools)!);
        var system = new FakeSystemDirectories(_temp.Path);

        var first = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);
        await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);
        Assert.True(_memory.Holds(Key(ToolDir)));
        Assert.Equal(2, Asked(ToolDir));

        var policy = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(2, Asked(ToolDir));
        Assert.Equal(2, Asked(PythonDir));
        Assert.False(first.MayRemove(movedTools).IsAllowed);
        Assert.False(policy.MayRemove(movedTools).IsAllowed);
        Assert.False(policy.MayRemove(Python).IsAllowed);
    }

    /// <summary>
    /// A failed <c>cache dir</c> is no answer, and the default it falls back to is not kept. The other
    /// two folders, which uv did answer, are questions of their own and are kept. The failure comes
    /// after trusted answers, from an ask an input change forced, so a failure kept as an answer would
    /// read the same inputs before and after and be served to the next pass.
    /// </summary>
    [Fact]
    public async Task AFailedQueryIsNotKept()
    {
        CreateCache(MovedCache);
        var provider = CreateProvider();
        await TrustAsync(provider);

        Write(UserConfig, "native-tls = true");
        _runner.Responding(_uv, CacheDir, string.Empty, exitCode: 2);
        provider.InvalidateCaches();
        await DeclaredAsync(provider);
        Assert.False(_memory.Holds(Key(CacheDir)));
        Assert.True(_memory.Holds(Key(ToolDir)));

        Answering(MovedCache);
        provider.InvalidateCaches();
        var places = await DeclaredAsync(provider);

        Assert.Contains(places, place => place.Path.Equals(MovedCache, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(1, Asked(ToolDir));
    }

    /// <summary>
    /// The plan checks the cache against uv's own folders before it sends <c>uv cache clean</c>, which
    /// removes the whole cache folder, so it asks uv afresh even though the memory holds answers whose
    /// inputs read the same. Here something no declared input records has set the cache to the folder
    /// that holds uv's tools: a plan from the memory would clean the old cache and the clean would take
    /// the tools (§5.2). The fresh plan declines and still names every survivor (§5.6).
    /// </summary>
    [Fact]
    public async Task ThePlanAsksUvAfreshAndDeclinesACacheMovedOntoItsToolsEvenWhenTheMemoryHoldsTheOldOne()
    {
        var provider = CreateProvider();
        await TrustAsync(provider);
        provider.InvalidateCaches();

        var holdingTools = Path.Combine(_environment.RoamingAppData, "uv");
        Answering(holdingTools);
        Assert.Contains(await DeclaredAsync(provider), place => place.Path.Equals(DefaultCache, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, Asked(CacheDir));

        var plan = await provider.PlanAsync();

        Assert.Equal(1, Asked(CacheDir));
        Assert.Equal(1, Asked(ToolDir));
        Assert.Equal(1, Asked(PythonDir));
        Assert.Empty(plan.Steps);
        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, note => note.Message.Contains("offers nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6 when the cache moved between two passes, as a <c>uv.toml</c> edit moves it: the plan measures
    /// the new cache and not the old, targets nothing, and still names uv's own folders as what must
    /// survive; and the declarations follow it.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheCacheMovedMeasuresItAndProtectsUvsOwnFolders()
    {
        CreateCache(MovedCache);
        var provider = CreateProvider();
        await TrustAsync(provider);

        Write(UserConfig, "cache-dir = \"" + MovedCache.Replace('\\', '/') + "\"");
        Answering(MovedCache);
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        var clean = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal([MovedCache], clean.MeasuredPaths);

        foreach (var survivor in new[] { provider.LocalRoot, Tools, Python, UserConfig })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.Contains(await DeclaredAsync(provider), place => place.Path.Equals(MovedCache, StringComparison.OrdinalIgnoreCase));
    }
}
