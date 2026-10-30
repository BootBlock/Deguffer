using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1 with a command that empties every folder its settings name. The replies are Composer 2.4.4's
/// as observed: one value per setting on stdout, with forward slashes.
/// </summary>
public sealed class ComposerCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ComposerCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Composer => _environment.FindExecutable("composer")!;

    private string Home => Path.Combine(_environment.RoamingAppData, "Composer");

    private string Cache => Path.Combine(_environment.LocalAppData, "Composer");

    private string WorkingDirectory => Path.Combine(_environment.LocalAppData, "Deguffer", "tool-working-directory");

    private ComposerCacheProvider CreateProvider(FakeProcessRunner runner) =>
        new(_environment, runner, FakeProcessInspector.NothingRunning, system: _system);

    /// <summary>
    /// Composer installed, answering each setting as <paramref name="overrides"/> says and otherwise as
    /// its defaults do, and emptying every cache folder when asked to clear, as <c>Cache::clear</c> does.
    /// </summary>
    private FakeProcessRunner ComposerAnswering(Dictionary<string, string>? overrides = null, int failing = -1)
    {
        _environment.WithExecutable("composer");

        var settings = new Dictionary<string, string>
        {
            ["home"] = Home,
            ["data-dir"] = Home,
            ["cache-dir"] = Cache,
            ["cache-files-dir"] = Path.Combine(Cache, "files"),
            ["cache-repo-dir"] = Path.Combine(Cache, "repo"),
            ["cache-vcs-dir"] = Path.Combine(Cache, "vcs"),
            ["cache-read-only"] = string.Empty,
        };

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        var asked = 0;

        return new FakeProcessRunner().Replying(Composer, arguments =>
        {
            if (arguments == $"clear-cache --no-interaction --working-dir \"{WorkingDirectory}\"")
            {
                foreach (var entry in new DirectoryInfo(Cache).EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo folder)
                    {
                        folder.Delete(recursive: true);
                    }
                    else
                    {
                        entry.Delete();
                    }
                }

                return new CommandOutcome(0, string.Empty, "Clearing cache (cache-dir): ...\nAll caches cleared.\n");
            }

            var prefix = $"config --global --absolute --no-interaction --working-dir \"{WorkingDirectory}\" ";
            Assert.StartsWith(prefix, arguments, StringComparison.Ordinal);

            var key = arguments[prefix.Length..];

            return asked++ == failing
                ? new CommandOutcome(1, string.Empty, "[RuntimeException] failed")
                : new CommandOutcome(0, settings[key].Replace('\\', '/') + "\n", string.Empty);
        });
    }

    private void PopulateCache()
    {
        _temp.CreateFile(4096, Path.GetRelativePath(_temp.Path, Path.Combine(Cache, "files", "vendor", "package", "abc.zip")));
        _temp.CreateFile(1024, Path.GetRelativePath(_temp.Path, Path.Combine(Cache, "repo", "https---repo.packagist.org", "packages.json")));
        File.WriteAllText(Path.Combine(Cache, ".htaccess"), "Deny from all");
        Directory.CreateDirectory(Home);
        File.WriteAllText(Path.Combine(Home, "auth.json"), "{\"github-oauth\": {\"github.com\": \"<YOUR_TOKEN>\"}}");
        File.WriteAllText(Path.Combine(Home, "config.json"), "{}");
    }

    [Fact]
    public async Task ReportsNotPresentWhenComposerIsNotInstalled()
    {
        var provider = CreateProvider(new FakeProcessRunner());

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansComposersClearCacheFromDeguffersOwnFolder()
    {
        PopulateCache();
        var runner = ComposerAnswering();

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Composer, command.FileName);
        Assert.Equal($"clear-cache --no-interaction --working-dir \"{WorkingDirectory}\"", command.Arguments);
        Assert.Equal([Cache], command.MeasuredPaths);
        Assert.True(command.EstimatedBytes >= 5120);
        Assert.Equal(CancelledCommand.Finishes, command.WhenCancelled);
        Assert.All(runner.Invocations, call => Assert.Contains($"--working-dir \"{WorkingDirectory}\"", call.Arguments, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersNothingWhereComposerDoesNotAnswerEverySetting()
    {
        PopulateCache();

        for (var failing = 0; failing < 7; failing++)
        {
            var plan = await CreateProvider(ComposerAnswering(failing: failing)).PlanAsync();

            Assert.True(plan.WasNotExamined, $"Query {failing} failed and the plan was examined anyway.");
            Assert.Empty(plan.Steps);
        }
    }

    /// <summary>
    /// clear-cache empties every folder a cache setting names. One moved outside the cache folder could
    /// hold anything, and the command cannot be told to leave it.
    /// </summary>
    [Theory]
    [InlineData("cache-files-dir")]
    [InlineData("cache-repo-dir")]
    [InlineData("cache-vcs-dir")]
    public async Task LeavesTheCacheAloneWhereASettingSendsTheClearOutsideIt(string setting)
    {
        PopulateCache();
        var elsewhere = _temp.CreateDirectory("Projects");
        var runner = ComposerAnswering(new() { [setting] = elsewhere });

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(setting, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsADisabledSettingAsOneTheClearSkips()
    {
        PopulateCache();

        var plan = await CreateProvider(ComposerAnswering(new() { ["cache-vcs-dir"] = "nul" })).PlanAsync();

        Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
    }

    [Fact]
    public async Task OffersNothingWhereTheCacheIsTurnedOff()
    {
        var plan = await CreateProvider(ComposerAnswering(new() { ["cache-dir"] = "nul" })).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Steps);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public async Task OffersNothingWhereTheCacheIsReadOnly(string answer)
    {
        PopulateCache();

        var plan = await CreateProvider(ComposerAnswering(new() { ["cache-read-only"] = answer })).PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>The cache set to Composer's home would have clear-cache empty auth.json with it.</summary>
    [Fact]
    public async Task LeavesACacheThatHoldsComposersHomeAlone()
    {
        PopulateCache();
        var runner = ComposerAnswering(new()
        {
            ["home"] = Path.Combine(Cache, "home"),
            ["data-dir"] = Path.Combine(Cache, "home"),
        });

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("credentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeavesACacheSetToTheProfileAlone()
    {
        PopulateCache();
        var profile = _environment.UserProfile;
        var runner = ComposerAnswering(new()
        {
            ["cache-dir"] = profile,
            ["cache-files-dir"] = Path.Combine(profile, "files"),
            ["cache-repo-dir"] = Path.Combine(profile, "repo"),
            ["cache-vcs-dir"] = Path.Combine(profile, "vcs"),
        });

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Empty(plan.Steps);
    }

    /// <summary>§5.2: clear-cache would remove an entry Composer never makes along with the rest.</summary>
    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("backups", true)]
    public async Task LeavesTheCacheAloneWhereItHoldsSomethingComposerDoesNotMake(string name, bool isFolder)
    {
        PopulateCache();
        var stranger = Path.Combine(Cache, name);

        if (isFolder)
        {
            Directory.CreateDirectory(stranger);
        }
        else
        {
            File.WriteAllText(stranger, "mine");
        }

        var plan = await CreateProvider(ComposerAnswering()).PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>§5.6: run as Composer runs it, the clear empties the cache, and the home and credentials outlive it.</summary>
    [Fact]
    public async Task VerifiesTheHomeAndCredentialsSurvivedTheClear()
    {
        PopulateCache();
        var provider = CreateProvider(ComposerAnswering());
        var auth = Path.Combine(Home, "auth.json");

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(auth, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Home, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.True(result.BytesReclaimed >= 5120);
        Assert.True(verification.Passed);
        Assert.True(File.Exists(auth));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
    }

    [Fact]
    public void ExploreRefusesComposersHomeAndAllowsOnlyTheCachesOwnFolders()
    {
        var policy = new ExploreActionPolicy([], CreateProvider(new FakeProcessRunner()).ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(Home).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Home, "auth.json")).IsAllowed);
        Assert.False(policy.MayRemove(Cache).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(Cache, "backups")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(Cache, "files")).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheTheClearIsSentTo()
    {
        PopulateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(ComposerAnswering())));
    }
}
