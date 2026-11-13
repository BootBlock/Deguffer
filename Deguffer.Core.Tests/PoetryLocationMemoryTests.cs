using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Poetry's two <c>config</c> answers are remembered across passes and Explore builds while everything
/// Poetry read to give them is unchanged, and asked again the moment any of it changes (#447). The
/// plan deletes <c>artifacts</c> by path, so it asks Poetry afresh whatever the memory holds.
///
/// <para>Every provider here shares one <see cref="ToolAnswerMemory"/> whose clock runs a minute ahead
/// of the files the fixture writes, as a real ask starts long after a configuration file was saved, so
/// an unchanged answer is always kept and only a changed input can explain a second ask.</para>
/// </summary>
public sealed class PoetryLocationMemoryTests : IDisposable
{
    private const string CacheDir = "config cache-dir";
    private const string VirtualenvsPath = "config virtualenvs.path";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner = new();
    private readonly string _poetry;

    public PoetryLocationMemoryTests()
    {
        _poetry = Path.Combine(_temp.CreateDirectory("bin"), "poetry.exe");
        File.WriteAllText(_poetry, "poetry 1.8");

        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("poetry", _poetry);
        _memory = new ToolAnswerMemory(_clock);

        CreateCache(DefaultCache);
        Answering(DefaultCache);
        _runner.Responding(_poetry, "cache list", "PyPI");
    }

    public void Dispose() => _temp.Dispose();

    private string DefaultCache => Path.Combine(_environment.LocalAppData, "pypoetry", "Cache");

    private string MovedCache => Path.Combine(_environment.UserProfile, "poetry-cache");

    private string RoamingConfig => Path.Combine(_environment.RoamingAppData, "pypoetry", "config.toml");

    private int Asked(string setting) => _runner.Invocations.Count(invocation => invocation.Arguments.StartsWith(setting, StringComparison.Ordinal));

    /// <summary>Poetry's answer from now on: its cache at <paramref name="cache"/>, the environments in it.</summary>
    private void Answering(string cache) =>
        _runner
            .Responding(_poetry, CacheDir, cache)
            .Responding(_poetry, VirtualenvsPath, Path.Combine(cache, "virtualenvs"));

    /// <summary>
    /// Every write happens before the first ask unless the test makes it after, so the clock is moved
    /// a minute past the moment it was called each time a provider is built.
    /// </summary>
    private PoetryCacheProvider CreateProvider()
    {
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));

        return new PoetryCacheProvider(_environment, _runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory(), memory: _memory);
    }

    /// <summary>The layout Poetry builds, and a child of the cache folder Deguffer does not recognise.</summary>
    private static void CreateCache(string root)
    {
        Write(Path.Combine(root, "artifacts", "ab", "cd", "package.whl"), new string('x', 4096));
        Write(Path.Combine(root, "cache", "repositories", "PyPI", "metadata.json"), new string('x', 2048));
        Write(Path.Combine(root, "virtualenvs", "myproject-py3.12", "Lib", "installed.pyd"), new string('x', 8192));
        Write(Path.Combine(root, "something-unrecognised", "notes.txt"), "kept");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Asks for both declarations Explore and a duplicate search read, which read the memory.</summary>
    private static async Task<string> DeclaredCacheAsync(PoetryCacheProvider provider)
    {
        var roots = await provider.DiscoverToolRootsAsync();
        await provider.CleanedPlacesAsync();

        return roots[0].Path;
    }

    [Fact]
    public async Task ASecondPassAfterAnInvalidationWithNothingChangedStartsNoLocationQuery()
    {
        var provider = CreateProvider();

        await DeclaredCacheAsync(provider);
        provider.InvalidateCaches();
        var cache = await DeclaredCacheAsync(provider);

        Assert.Equal(1, Asked(CacheDir));
        Assert.Equal(1, Asked(VirtualenvsPath));
        Assert.Equal(DefaultCache, cache, StringComparer.OrdinalIgnoreCase);
    }

    public enum Change
    {
        Create,
        Write,
        Delete,
    }

    /// <summary>
    /// Every file Poetry reads to say where it keeps things, relative to the temp tree: its
    /// configuration folder, and both project files in the folder it runs in and every one above it
    /// (sampled here at the working folder itself, the profile and the temp root).
    /// </summary>
    public static TheoryData<string, Change> DeclaredInputs()
    {
        string[] inputs =
        [
            @"profile\AppData\Roaming\pypoetry\config.toml",
            @"profile\AppData\Roaming\pypoetry\auth.toml",
            @"profile\AppData\Local\Deguffer\tool-working-directory\pyproject.toml",
            @"profile\AppData\Local\Deguffer\tool-working-directory\poetry.toml",
            @"profile\pyproject.toml",
            @"profile\poetry.toml",
            "pyproject.toml",
            "poetry.toml",
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
    /// <c>POETRY_CONFIG_DIR</c> moves the configuration folder, taken as it stands (a relative value
    /// from the folder Poetry runs in), and the roaming one is then not read.
    /// </summary>
    [Theory]
    [InlineData("absolute", "config.toml")]
    [InlineData("absolute", "auth.toml")]
    [InlineData("relative", "config.toml")]
    [InlineData("relative", "auth.toml")]
    public async Task ChangingAFileInTheConfigurationFolderPoetryConfigDirNamesAsksAgain(string form, string name)
    {
        var folder = form == "absolute"
            ? Path.Combine(_temp.Path, "custom-config")
            : Path.Combine(ToolInputs.WorkingDirectory(_environment), "custom-config");
        _environment.WithToolEnvironmentVariable("POETRY_CONFIG_DIR", form == "absolute" ? folder : "custom-config");

        await AssertChangingAsksAgainAsync(Path.Combine(folder, name), Change.Write);
    }

    private async Task AssertChangingAsksAgainAsync(string path, Change change)
    {
        if (change is not Change.Create)
        {
            Write(path, "[virtualenvs]");
        }

        var provider = CreateProvider();
        await DeclaredCacheAsync(provider);
        provider.InvalidateCaches();
        await DeclaredCacheAsync(provider);
        Assert.Equal(1, Asked(CacheDir));

        switch (change)
        {
            case Change.Create:
            case Change.Write:
                Write(path, "[virtualenvs]\nin-project = true");
                break;
            case Change.Delete:
                File.Delete(path);
                break;
        }

        provider.InvalidateCaches();
        await DeclaredCacheAsync(provider);

        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(2, Asked(VirtualenvsPath));
    }

    [Fact]
    public async Task AVariableSetInTheToolEnvironmentAsksAgain()
    {
        var provider = CreateProvider();
        await DeclaredCacheAsync(provider);

        _environment.WithToolEnvironmentVariable("POETRY_CACHE_DIR", MovedCache);
        Answering(MovedCache);
        provider.InvalidateCaches();

        Assert.Equal(MovedCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, Asked(CacheDir));
    }

    [Fact]
    public async Task TheExecutableReplacedAsksAgain()
    {
        var provider = CreateProvider();
        await DeclaredCacheAsync(provider);

        File.WriteAllText(_poetry, "poetry 2.1, a newer build");
        provider.InvalidateCaches();
        await DeclaredCacheAsync(provider);

        Assert.Equal(2, Asked(CacheDir));
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second build is answered from the
    /// memory the first filled, and still refuses everything Poetry reported.
    /// </summary>
    [Fact]
    public async Task ASecondExploreBuildAnswersFromMemoryWithoutAskingPoetry()
    {
        CreateCache(MovedCache);
        Answering(MovedCache);
        var system = new FakeSystemDirectories(_temp.Path);

        var first = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [CreateProvider()]);
        var second = CreateProvider();
        var policy = await ExploreActionPolicy.ForAsync(system, _environment, new FakeVolumeInventory(), [second]);
        var places = await second.CleanedPlacesAsync();

        Assert.Equal(1, Asked(CacheDir));
        Assert.Equal(1, Asked(VirtualenvsPath));
        Assert.False(first.MayRemove(MovedCache).IsAllowed);
        Assert.False(policy.MayRemove(MovedCache).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(MovedCache, "virtualenvs", "myproject-py3.12")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(MovedCache, "something-unrecognised")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(MovedCache, "artifacts")).IsAllowed);
        Assert.Contains(places, place => place.Path.Equals(Path.Combine(MovedCache, "artifacts"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6 when the cache moved between two passes, as a <c>config.toml</c> edit moves it: the plan,
    /// which asks Poetry afresh every time, targets and protects the new cache, and nothing of the old
    /// one is left in it as a target or as a survivor that would mask the new one.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheCacheMovedTargetsAndProtectsTheNewOne()
    {
        CreateCache(MovedCache);
        var provider = CreateProvider();
        await provider.PlanAsync();

        Write(RoamingConfig, "cache-dir = \"poetry-cache\"");
        Answering(MovedCache);
        provider.InvalidateCaches();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(MovedCache, "artifacts")], plan.TargetedPaths);
        Assert.Equal(MovedCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);

        string[] survivors =
        [
            MovedCache,
            Path.Combine(MovedCache, "virtualenvs"),
            Path.Combine(MovedCache, "cache"),
            Path.Combine(MovedCache, "something-unrecognised"),
            RoamingConfig,
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(plan.ProtectedPaths, p => LongPath.Contains(DefaultCache, p.Path));
        Assert.All(plan.Steps.OfType<RunCommandStep>(), step =>
            Assert.All(step.MeasuredPaths, path => Assert.True(LongPath.Contains(MovedCache, path), path)));

        // The deletion overreaching into the new cache's environments is caught.
        Directory.Delete(Path.Combine(MovedCache, "virtualenvs"), recursive: true);
        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, failure => failure.Subject.Equals(Path.Combine(MovedCache, "virtualenvs"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A failed <c>config cache-dir</c> is no answer, and the default it falls back to is not kept. The
    /// environments' setting, which Poetry did answer, is a question of its own and is kept.
    /// </summary>
    [Fact]
    public async Task AFailedQueryIsNotKept()
    {
        _runner.Responding(_poetry, CacheDir, string.Empty, exitCode: 1);
        var provider = CreateProvider();
        Assert.Equal(DefaultCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);

        Answering(MovedCache);
        provider.InvalidateCaches();

        Assert.Equal(MovedCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(1, Asked(VirtualenvsPath));
    }

    /// <summary>
    /// The plan deletes <c>artifacts</c> by path, so it asks Poetry afresh even though the memory
    /// holds an answer whose inputs read the same, and even when a declaration earlier in the same
    /// pass was answered from the memory. An input the memory missed would otherwise move a deletion
    /// (§5.2). The fresh answer is then what the memory holds.
    /// </summary>
    [Fact]
    public async Task ThePlanAsksPoetryAfreshEvenWhenTheMemoryHoldsTheAnswer()
    {
        CreateCache(MovedCache);
        var provider = CreateProvider();
        await DeclaredCacheAsync(provider);
        provider.InvalidateCaches();

        // Moved by something no declared input records, and the declaration still reads the memory.
        Answering(MovedCache);
        Assert.Equal(DefaultCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, Asked(CacheDir));

        var plan = await provider.PlanAsync();

        Assert.Equal(2, Asked(CacheDir));
        Assert.Equal(2, Asked(VirtualenvsPath));
        Assert.Equal([Path.Combine(MovedCache, "artifacts")], plan.TargetedPaths);
        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(DefaultCache, path));

        provider.InvalidateCaches();

        Assert.Equal(MovedCache, await DeclaredCacheAsync(provider), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, Asked(CacheDir));
    }
}
