using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// uv is a §5.1 provider, so most of what matters is that it asks the tool rather than assuming.
/// The rest carries §5.6 for the case that makes uv different from npm: the tools, interpreters and
/// credentials a clean must spare are in another profile from the cache, and a moved cache can overlap them.
/// </summary>
public sealed class UvCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public UvCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>The uv the fake environment resolves, so a test names the program a step must run.</summary>
    private string Uv => _environment.FindExecutable("uv")!;

    private UvCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>uv answering its cache query with nothing, so the documented default location stands.</summary>
    private FakeProcessRunner UvAnsweringNothing() =>
        new FakeProcessRunner().Responding(Uv, "cache dir", string.Empty);

    /// <summary>Create uv's state directory with a populated cache, and return the cache path.</summary>
    private string CreateCache(long bytes = 4096)
    {
        var cache = Path.Combine(_environment.LocalAppData, "uv", "cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[bytes]);
        return cache;
    }

    [Fact]
    public async Task ReportsNotPresentWhenUvWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task AsksUvWhereItsCacheIsRatherThanAssuming()
    {
        _environment.WithExecutable("uv");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", elsewhere);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("cache dir", StringComparison.Ordinal));
        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    [Fact]
    public async Task AsksUvNotToColouriseThePathItIsAboutToBeParsed()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var runner = UvAnsweringNothing();
        await CreateProvider(runner).PlanAsync();

        // Without this, uv emits ANSI escapes around the path and they land inside it.
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("--color never", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed <c>uv cache dir</c> is no answer, whatever it printed. The reply here fails and still names
    /// a populated folder that is not the default, so a provider that read the output without the exit
    /// code would measure that folder and say uv reported it.
    /// </summary>
    [Fact]
    public async Task SaysTheCacheIsTheDefaultWhenUvDidNotAnswer()
    {
        _environment.WithExecutable("uv");
        var cache = CreateCache();
        var printed = _temp.CreateDirectory("printed-cache");
        _temp.CreateFile(2048, "printed-cache", "payload.bin");

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", printed, exitCode: 1);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Equal([cache], Assert.Single(plan.Steps.OfType<RunCommandStep>()).MeasuredPaths);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("uv reports", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains($"uv did not say where its cache directory is, so {cache} is the documented default", StringComparison.Ordinal)
            && n.Message.Contains("uv's own command clears wherever uv really keeps it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaysUvReportedTheCacheWhenItDid()
    {
        _environment.WithExecutable("uv");
        var elsewhere = _temp.CreateDirectory("relocated-cache");
        _temp.CreateFile(2048, "relocated-cache", "payload.bin");

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", elsewhere);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Message == $"uv reports its cache directory as {elsewhere}.");
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("did not say", StringComparison.Ordinal));
    }

    /// <summary>A missing default says nothing about a cache uv keeps elsewhere.</summary>
    [Fact]
    public async Task SaysTheDefaultIsMissingRatherThanTheCacheWhenUvDidNotAnswer()
    {
        _environment.WithExecutable("uv");

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", string.Empty, exitCode: 1);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.StartsWith(
            "uv did not say where its cache directory is, and the documented default",
            Assert.Single(plan.Notes).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToTheDocumentedLocationWhenUvCannotAnswer()
    {
        _environment.WithExecutable("uv");
        var cache = CreateCache();

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", string.Empty, exitCode: 1);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
    }

    [Fact]
    public async Task EvictsWithUvsOwnCommandRatherThanDeletingThePath()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var plan = await CreateProvider(UvAnsweringNothing()).PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to evict.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Uv, step.FileName);
        Assert.Contains("cache clean", step.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotPassForceBecauseThatOverridesUvsOwnInUseCheck()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var plan = await CreateProvider(UvAnsweringNothing()).PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Uv, step.FileName);
        Assert.DoesNotContain("--force", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout uv really writes on Windows: the cache in the local profile, and the tools,
    /// interpreters, credentials and configuration in the roaming one.
    /// </summary>
    [Fact]
    public async Task NeverTargetsUvsStateBecauseToolsAndInterpretersLiveThere()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider(UvAnsweringNothing());
        var tools = _temp.CreateDirectory("profile", "AppData", "Roaming", "uv", "tools");
        _temp.CreateFile(512, "profile", "AppData", "Roaming", "uv", "tools", "ruff", "ruff.exe");
        var python = _temp.CreateDirectory("profile", "AppData", "Roaming", "uv", "python");
        var credentials = _temp.CreateFile(64, "profile", "AppData", "Roaming", "uv", "credentials", "credentials.toml");
        var configuration = _temp.CreateFile(64, "profile", "AppData", "Roaming", "uv", "uv.toml");

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.All(
            plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths),
            path => Assert.False(LongPath.Contains(path, provider.RoamingRoot) || LongPath.Contains(provider.RoamingRoot, path)));

        // Not merely unmentioned — asserted to survive (§5.6).
        foreach (var survivor in new[] { provider.LocalRoot, provider.RoamingRoot, tools, python, Path.GetDirectoryName(credentials)!, configuration })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }
    }

    /// <summary>
    /// uv never makes <c>tools</c> or <c>python</c> under the local profile, so their survival there
    /// proved nothing.
    /// </summary>
    [Fact]
    public async Task DoesNotProtectFoldersUvNeverMakesBesideTheCache()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider(UvAnsweringNothing());

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(provider.LocalRoot, "tools"), StringComparison.OrdinalIgnoreCase)
            || p.Path.Equals(Path.Combine(provider.LocalRoot, "python"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProtectsTheToolAndPythonFoldersUvReports()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var tools = _temp.CreateDirectory("elsewhere", "uv-tools");
        var python = _temp.CreateDirectory("elsewhere", "uv-python");
        var runner = UvAnsweringNothing().Responding(Uv, "tool dir", tools).Responding(Uv, "python dir", python);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments == "tool dir --color never");
        Assert.Contains(runner.Invocations, i => i.Arguments == "python dir --color never");
        Assert.Contains(plan.ProtectedPaths, p => p.Path == tools && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == python && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// An install from before late 2024 keeps its state in <c>%APPDATA%\uv\data</c>, and uv keeps using it
    /// wherever it exists. Elsewhere the state is in <c>%APPDATA%\uv</c> itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FallsBackToTheLegacyStateFolderOnlyWhereItExists(bool legacy)
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider(UvAnsweringNothing());

        if (legacy)
        {
            Directory.CreateDirectory(Path.Combine(provider.RoamingRoot, "data"));
        }

        var plan = await provider.PlanAsync();

        var used = legacy ? Path.Combine(provider.RoamingRoot, "data") : provider.RoamingRoot;
        var unused = legacy ? provider.RoamingRoot : Path.Combine(provider.RoamingRoot, "data");

        foreach (var bucket in new[] { "tools", "python", "credentials" })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(used, bucket));
            Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path == Path.Combine(unused, bucket));
        }
    }

    /// <summary>A refusal is no evidence that the legacy folder is absent, so both candidates survive.</summary>
    [Fact]
    public async Task ProtectsBothStateFoldersWhenWindowsWillNotSayWhichIsUsed()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider(UvAnsweringNothing());
        var legacy = _temp.CreateDirectory("profile", "AppData", "Roaming", "uv", "data");

        using var denied = DeniedDirectory.WithUnreadableAttributes(legacy);

        var plan = await provider.PlanAsync();

        foreach (var root in new[] { legacy, provider.RoamingRoot })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(root, "tools"));
            Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(root, "python"));
            Assert.Contains(plan.ProtectedPaths, p => p.Path == Path.Combine(root, "credentials"));
        }
    }

    /// <summary>
    /// <c>uv cache clean</c> removes the whole cache folder, so a cache set inside a state folder, or to
    /// a folder holding one, would be cleaned with what is around or under it.
    /// </summary>
    [Theory]
    [InlineData("tools-inside")]
    [InlineData("state-root")]
    [InlineData("holds-roaming")]
    [InlineData("holds-local")]
    public async Task OffersNothingWhenTheCacheOverlapsUvsOwnFolders(string shape)
    {
        _environment.WithExecutable("uv");
        var provider = CreateProvider();
        var tools = _temp.CreateDirectory("elsewhere", "uv-tools");
        var cache = shape switch
        {
            "tools-inside" => Path.Combine(tools, "cache"),
            "state-root" => provider.RoamingRoot,
            "holds-roaming" => _environment.RoamingAppData,
            _ => _environment.LocalAppData,
        };
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[2048]);
        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", cache).Responding(Uv, "tool dir", tools);
        provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Steps);
        Assert.Contains("overlaps", Assert.Single(plan.Notes).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await provider.CleanedPlacesAsync(), place => place.Path == cache);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheInstalledToolsVanished()
    {
        _environment.WithExecutable("uv");
        CreateCache();
        var provider = CreateProvider(UvAnsweringNothing());
        var tools = _temp.CreateDirectory("profile", "AppData", "Roaming", "uv", "tools");

        var plan = await provider.PlanAsync();
        Directory.Delete(tools, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(tools, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §7.1 refuses the whole roaming folder, and everything in it, because none of it is a cache.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("tools")]
    [InlineData("python")]
    [InlineData("credentials")]
    [InlineData("uv.toml")]
    [InlineData(@"data\tools")]
    public void ExploreRefusesEverythingInTheRoamingFolder(string relative)
    {
        var provider = CreateProvider();
        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(Path.Combine(provider.RoamingRoot, relative)).IsAllowed);
    }

    [Fact]
    public async Task DeclaresTheToolAndPythonFoldersUvReportsForExplore()
    {
        _environment.WithExecutable("uv");
        var tools = _temp.CreateDirectory("elsewhere", "uv-tools");
        var python = _temp.CreateDirectory("elsewhere", "uv-python");
        var runner = UvAnsweringNothing().Responding(Uv, "tool dir", tools).Responding(Uv, "python dir", python);

        var discovered = await CreateProvider(runner).DiscoverToolRootsAsync();

        Assert.Equal([tools, python], discovered.Select(root => root.Path));
        Assert.All(discovered, root => Assert.False(root.Recognises(new ToolRootChild("ruff", ChildKind.Folder))));
    }

    [Fact]
    public async Task DoesNotRequireTheCacheDirectoryToSurviveBecauseUvRemovesIt()
    {
        _environment.WithExecutable("uv");
        var cache = CreateCache();

        var plan = await CreateProvider(UvAnsweringNothing()).PlanAsync();

        // 'uv cache clean' deletes the cache root rather than emptying it, and recreates it on next
        // use. Protecting it would fail verification on a successful run.
        Assert.DoesNotContain(plan.ProtectedPaths, p =>
            p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheLocalFolderVanished()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var provider = CreateProvider(UvAnsweringNothing());
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch: clearing the cache took uv with it.
        Directory.Delete(provider.LocalRoot, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Subject.Equals(provider.LocalRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        _environment.WithExecutable("uv");
        var first = CreateCache();

        var moved = Path.Combine(_temp.Path, "moved-cache");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", first);
        var provider = CreateProvider(runner);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));

        // UV_CACHE_DIR moved between scans; the planner invalidates before replanning.
        runner.Responding(Uv, "cache dir", moved);
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(moved));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));
    }

    [Fact]
    public async Task WarnsWhenUvIsRunning()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        var provider = new UvCacheProvider(
            _environment, UvAnsweringNothing(), new FakeProcessInspector("uv"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task SaysSoWhenUvIsInstalledButHasNeverCachedAnything()
    {
        _environment.WithExecutable("uv");

        var reported = Path.Combine(_environment.LocalAppData, "uv", "cache");
        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", reported);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheDefaultCacheTheCommandIsSentTo()
    {
        _environment.WithExecutable("uv");
        CreateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(UvAnsweringNothing())));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheUvReportsElsewhere()
    {
        _environment.WithExecutable("uv");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding(Uv, "cache dir", elsewhere);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider(runner)));
    }
}
