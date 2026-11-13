using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// npm's answer about where its cache is outlives a pass while everything npm read to give it is
/// unchanged, and is asked again the moment any of it changes (#447).
///
/// <para>The plan only runs npm's own clean (§5.1), so it plans from the remembered answer: a missed
/// input could misstate the figure, never move what is removed. Each declared input still has a case
/// here, because a missed one is a moved cache measured where npm no longer keeps it.</para>
/// </summary>
public sealed class NpmCacheProviderMemoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly ManualTimeProvider _clock = new();
    private readonly ToolAnswerMemory _memory;
    private readonly FakeProcessRunner _runner = new();
    private readonly string _npm;
    private readonly string _cache;

    public NpmCacheProviderMemoryTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _memory = new ToolAnswerMemory(_clock);

        _npm = Write("nodejs", "npm.cmd");
        Write("nodejs", "node.exe");
        Write("nodejs", "node_modules", "npm", "package.json");
        Write("nodejs", "node_modules", "npm", "npmrc");
        _environment.WithExecutable("npm", _npm).WithExecutable("node", Write("path-node", "node.exe"));
        Write(".npmrc");
        Write("config", "user.npmrc");
        Directory.CreateDirectory(ToolInputs.WorkingDirectory(_environment));

        _cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        Write("AppData", "Local", "npm-cache", "_cacache", "content-v2", "blob");
        _runner.Responding(_npm, "config get", NpmConfig(_cache));

        // Every ask starts after the fixture was written, as a real one starts long after a
        // configuration file was last saved.
        _clock.Advance(DateTimeOffset.UtcNow - _clock.GetUtcNow() + TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Every input npm's answer is declared to depend on, relative to the fake profile.</summary>
    public static TheoryData<string> DeclaredInputs() =>
    [
        Path.Combine("nodejs", "node.exe"),
        Path.Combine("path-node", "node.exe"),
        Path.Combine("nodejs", "node_modules", "npm", "package.json"),
        Path.Combine("nodejs", "node_modules", "npm", "npmrc"),
        Path.Combine("AppData", "Roaming", "npm", "node_modules", "npm", "package.json"),
        Path.Combine("AppData", "Roaming", "npm", "node_modules", "npm", "npmrc"),
        ".npmrc",
        Path.Combine("config", "user.npmrc"),
        Path.Combine("AppData", "Roaming", "npm", "etc", "npmrc"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", ".npmrc"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", "package.json"),
        Path.Combine("AppData", "Local", "Deguffer", "tool-working-directory", "node_modules"),
        Path.Combine("AppData", "Local", ".npmrc"),
        "package.json",
        "node_modules",
    ];

    [Fact]
    public async Task ASecondPassAfterInvalidationWithNothingChangedStartsNoQuery()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        var second = await provider.PlanAsync();

        Assert.Equal(1, Asked);
        Assert.Contains(second.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(Path.Combine(_cache, "_cacache")));
    }

    /// <summary>Writing an input that is there, or creating one that is not, asks npm again.</summary>
    [Theory]
    [MemberData(nameof(DeclaredInputs))]
    public async Task ChangingADeclaredInputAsksAgain(string input)
    {
        var path = Path.Combine(_environment.UserProfile, input);
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();

        if (File.Exists(path))
        {
            File.AppendAllText(path, "changed");
        }
        else if (Path.GetFileName(path) == "node_modules")
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Write(input.Split(Path.DirectorySeparatorChar));
        }

        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Theory]
    [InlineData("config\\user.npmrc")]
    [InlineData("nodejs\\node_modules\\npm\\npmrc")]
    [InlineData("path-node\\node.exe")]
    public async Task DeletingADeclaredInputAsksAgain(string input)
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        File.Delete(Path.Combine(_environment.UserProfile, input));
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task AVariableSetInTheEnvironmentNpmIsStartedWithAsksAgain()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        _environment.WithToolEnvironmentVariable("npm_config_cache", Path.Combine(_temp.Path, "elsewhere"));
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    [Fact]
    public async Task ReplacingTheExecutableAsksAgain()
    {
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        var replacement = Path.Combine(_temp.Path, "npm.cmd.new");
        File.WriteAllText(replacement, "@echo a newer npm");
        File.Move(replacement, _npm, overwrite: true);
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// Explore builds a fresh set of providers for each policy. The second asks nothing, because the
    /// answer is kept by the memory of the runner they share, not by the provider that asked.
    /// </summary>
    [Fact]
    public async Task ASecondProviderOnTheSameMemoryAnswersCleanedPlacesWithoutAsking()
    {
        var moved = Path.Combine(_temp.Path, "elsewhere", "npm-cache");
        _runner.Responding(_npm, "config get", NpmConfig(moved));

        await Provider().CleanedPlacesAsync();
        var places = await Provider().CleanedPlacesAsync();

        Assert.Equal(1, Asked);
        Assert.Contains(places, p => p.Path == Path.Combine(moved, "_cacache"));
    }

    /// <summary>
    /// §5.6 when the cache moved between two passes: the second plan measures and protects the new
    /// cache, its <c>_npx</c> and npm's own folders, and leaves the old cache alone, and the run
    /// leaves everything beside what npm's clean removes.
    /// </summary>
    [Fact]
    public async Task APlanAfterTheCacheMovedTargetsAndProtectsTheNewCacheAndNotTheOld()
    {
        var provider = Provider();
        var oldContent = Path.Combine(_cache, "_cacache", "content-v2", "blob");
        await provider.PlanAsync();

        var moved = Path.Combine(_temp.Path, "elsewhere", "npm-cache");
        var npxPackage = WriteAt(moved, "_npx", "0123abcd", "node_modules", "tool.js");
        var sibling = WriteAt(moved, "unrecognised", "keep.bin");
        WriteAt(moved, "_cacache", "content-v2", "blob");
        File.AppendAllText(Path.Combine(_environment.UserProfile, ".npmrc"), $"cache={moved}");
        _runner
            .Responding(_npm, "config get", NpmConfig(moved))
            .Replying(_npm, arguments =>
            {
                if (arguments != "cache clean --force")
                {
                    return null;
                }

                Directory.Delete(Path.Combine(moved, "_cacache"), recursive: true);
                return new CommandOutcome(0, string.Empty, string.Empty);
            });
        provider.InvalidateCaches();

        var plan = await provider.PlanAsync();

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal([Path.Combine(moved, "_cacache")], command.MeasuredPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == moved);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(moved, "_npx"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(_environment.UserProfile, ".npmrc"));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(_environment.RoamingAppData, "npm"));
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.StartsWith(_cache, StringComparison.OrdinalIgnoreCase));

        await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.True(verification.Passed);
        Assert.True(File.Exists(npxPackage));
        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(oldContent));
        Assert.False(Directory.Exists(Path.Combine(moved, "_cacache")));
    }

    [Fact]
    public async Task AFailedQueryIsNotKept()
    {
        _runner.Responding(_npm, "config get", NpmConfig(_cache), exitCode: 1);
        var provider = Provider();

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
    }

    /// <summary>
    /// An answer that leaves out a file npm reads names no file to watch, so a change to it would go
    /// unnoticed: it is planned from and asked again.
    /// </summary>
    [Theory]
    [InlineData("prefix")]
    [InlineData("userconfig")]
    [InlineData("globalconfig")]
    public async Task AnAnswerMissingANamedPathIsNotKept(string missing)
    {
        var partial = string.Join('\n', NpmConfig(_cache).Split('\n').Where(line => !line.StartsWith(missing + "=", StringComparison.Ordinal)));
        _runner.Responding(_npm, "config get", partial);
        var provider = Provider();

        var first = await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, Asked);
        Assert.Contains(first.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(Path.Combine(_cache, "_cacache")));
    }

    private int Asked => _runner.Invocations.Count(i => i.Arguments.StartsWith("config get", StringComparison.Ordinal));

    private NpmCacheProvider Provider() =>
        new(_environment, _runner, FakeProcessInspector.NothingRunning, null, _memory);

    /// <summary>
    /// What npm prints for the query. The user configuration is moved out of the profile's root, as
    /// <c>NPM_CONFIG_USERCONFIG</c> moves it, so it is not also one of the folders up from where npm runs.
    /// </summary>
    private string NpmConfig(string cache) =>
        $"cache={cache}\n"
        + $"userconfig={Path.Combine(_environment.UserProfile, "config", "user.npmrc")}\n"
        + $"globalconfig={Path.Combine(_environment.RoamingAppData, "npm", "etc", "npmrc")}\n"
        + $"prefix={Path.Combine(_environment.RoamingAppData, "npm")}\n";

    /// <summary>A small file at <paramref name="segments"/> under the fake profile.</summary>
    private string Write(params string[] segments) => WriteAt(_environment.UserProfile, segments);

    private static string WriteAt(string folder, params string[] segments)
    {
        var path = Path.Combine([folder, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }
}
