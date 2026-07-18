using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// PlatformIO is the first Tier 2 provider, so alongside the §5.1 and §5.6 rules these cover the
/// thing that makes it Tier 2 at all: the tier travels into the plan, where §7's confirmation is
/// derived from it.
///
/// The subject is a core directory whose disposable cache is a small fraction of its size, sitting
/// beside gigabytes of installed toolchain. That is precisely the shape a size-driven rule gets
/// catastrophically wrong, so the negative assertions carry most of the weight here.
/// </summary>
public sealed class PlatformIoCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PlatformIoCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PlatformIoCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CoreRoot => Path.Combine(_environment.UserProfile, ".platformio");

    /// <summary>The cache in its default place, populated.</summary>
    private string CreateCache(long bytes = 4096)
    {
        var cache = Path.Combine(CoreRoot, ".cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[bytes]);
        return cache;
    }

    /// <summary>The expensive siblings: installed toolchains, the interpreter, and user libraries.</summary>
    private string[] CreateInstalledToolchains()
    {
        string[] siblings =
        [
            Path.Combine(CoreRoot, "packages"),
            Path.Combine(CoreRoot, "platforms"),
            Path.Combine(CoreRoot, "penv"),
            Path.Combine(CoreRoot, "python3"),
            Path.Combine(CoreRoot, "lib"),
        ];

        foreach (var sibling in siblings)
        {
            Directory.CreateDirectory(sibling);
            File.WriteAllBytes(Path.Combine(sibling, "payload.bin"), new byte[8192]);
        }

        return siblings;
    }

    private static string InfoJson(string? coreDir = null, string? cacheDir = null)
    {
        var fields = new Dictionary<string, object>();

        if (coreDir is not null)
        {
            fields["core_dir"] = coreDir;
        }

        if (cacheDir is not null)
        {
            fields["cache_dir"] = cacheDir;
        }

        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task ReportsNotPresentWhenPlatformIoWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>The tier is the product (§3), and §7 derives the confirmation from it.</summary>
    [Fact]
    public async Task IsTier2AndCarriesThatIntoThePlanAndItsConfirmation()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);

        var plan = await provider.PlanAsync();
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);

        // Tier 2 is offered but never pre-selected, and needs a deliberate yes before it runs.
        Assert.False(plan.Tier.IsPreSelectedByDefault());
        Assert.Equal(ConfirmationLevel.Acknowledgement, ConfirmationRequirement.For(plan).Level);
    }

    [Fact]
    public async Task AsksPlatformIoWhereItsCacheIsRatherThanAssuming()
    {
        _environment.WithExecutable("pio");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: elsewhere));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("--json-output", StringComparison.Ordinal));
        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    /// <summary>
    /// PLATFORMIO_CORE_DIR relocates the whole core directory, and the cache goes with it even when
    /// cache_dir itself is not reported.
    /// </summary>
    [Fact]
    public async Task DerivesTheCacheFromARelocatedCoreDirectoryWhenOnlyThatIsReported()
    {
        _environment.WithExecutable("pio");
        var relocatedCore = Path.Combine(_temp.Path, "elsewhere", ".platformio");
        var relocatedCache = Path.Combine(relocatedCore, ".cache");
        Directory.CreateDirectory(relocatedCache);
        File.WriteAllBytes(Path.Combine(relocatedCache, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(coreDir: relocatedCore));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(relocatedCache));
    }

    /// <summary>Some versions wrap each value as {"value": …, "default": …}.</summary>
    [Fact]
    public async Task ReadsTheWrappedValueShapeAsWellAsThePlainOne()
    {
        _environment.WithExecutable("pio");
        var elsewhere = Path.Combine(_temp.Path, "wrapped-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var json =
            $$$"""{"cache_dir": {"value": {{{JsonSerializer.Serialize(elsewhere)}}}, "default": null}}""";
        var runner = new FakeProcessRunner().Responding("system info", json);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Usage: pio system info [OPTIONS]")] // an older build that ignores --json-output
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("{\"core_dir\": null}")]
    [InlineData("{\"cache_dir\": \"not-a-rooted-path\"}")]
    public async Task FallsBackToTheDocumentedLocationWhenPlatformIoCannotAnswer(string output)
    {
        _environment.WithExecutable("pio");
        var cache = CreateCache();

        var runner = new FakeProcessRunner().Responding("system info", output);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
    }

    [Fact]
    public async Task EvictsWithPlatformIosOwnCommandRatherThanDeletingThePath()
    {
        _environment.WithExecutable("pio");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to evict.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("system prune", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scoping flag is the safety property. An unscoped prune also removes "unnecessary" core
    /// and platform packages — a judgement about installed toolchains that is the user's to make.
    /// </summary>
    [Fact]
    public async Task ScopesPruneToTheCacheSoInstalledPackagesAreNeverItsBusiness()
    {
        _environment.WithExecutable("pio");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("--cache", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--core-packages", step.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--platform-packages", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6, and the case this provider exists to get right: the toolchains are most of the core
    /// directory's size, and none of them may be touched to reclaim the cache beside them.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheCoreRootOrTheInstalledToolchainsBesideTheCache()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        var siblings = CreateInstalledToolchains();
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.CoreRoot, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var measured = plan.Steps.OfType<RunCommandStep>().SelectMany(s => s.MeasuredPaths).ToList();

        foreach (var sibling in siblings.Append(provider.CoreRoot))
        {
            Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(measured, path => Assert.NotEqual(
                sibling.TrimEnd(Path.DirectorySeparatorChar),
                path.TrimEnd(Path.DirectorySeparatorChar),
                StringComparer.OrdinalIgnoreCase));

            // Not merely unmentioned — asserted to survive (§5.6).
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheInstalledPackagesVanished()
    {
        _environment.WithExecutable("pio");
        CreateCache();
        CreateInstalledToolchains();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch: the prune took the toolchains with it.
        var packages = Path.Combine(provider.CoreRoot, "packages");
        Directory.Delete(packages, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c =>
            c.Path.Equals(packages, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReResolvesTheCacheDirectoryAfterInvalidationBecauseItCanMove()
    {
        _environment.WithExecutable("pio");
        var first = CreateCache();

        var moved = Path.Combine(_temp.Path, "moved-cache");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: first));
        var provider = CreateProvider(runner);

        var before = await provider.PlanAsync();
        Assert.Contains(before.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));

        runner.Responding("system info", InfoJson(cacheDir: moved));
        provider.InvalidateCaches();

        var after = await provider.PlanAsync();

        Assert.Contains(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(moved));
        Assert.DoesNotContain(after.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(first));
    }

    [Fact]
    public async Task WarnsWhenPlatformIoIsRunning()
    {
        _environment.WithExecutable("pio");
        CreateCache();

        var provider = new PlatformIoCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("pio"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task SaysSoWhenPlatformIoIsInstalledButHasNeverCachedAnything()
    {
        _environment.WithExecutable("pio");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// §6.3: a cache relocated under a path past MAX_PATH must still be measured, not silently
    /// skipped. Note this assertion is weaker on a machine with LongPathsEnabled set.
    /// </summary>
    [Fact]
    public async Task MeasuresACacheRelocatedBeyondMaxPath()
    {
        _environment.WithExecutable("pio");

        var deep = _temp.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        var cache = Path.Combine(deep, ".cache");
        Assert.True(cache.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(cache));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(cache, "payload.bin")), new byte[4096]);

        var runner = new FakeProcessRunner().Responding("system info", InfoJson(cacheDir: cache));
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
        Assert.True(plan.EstimatedBytes > 0, "A cache past MAX_PATH was measured as empty.");
    }
}
