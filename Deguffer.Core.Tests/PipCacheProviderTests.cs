using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// pip is a §5.1 provider, so what matters is that it asks pip where the cache is rather than
/// assuming, evicts with pip's own command, and leaves the configuration that sits above the cache
/// alone (§5.6).
/// </summary>
public sealed class PipCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PipCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PipCacheProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Create pip's default cache directory populated, and return its path.</summary>
    private string CreateCache(long bytes = 4096)
    {
        var cache = Path.Combine(_environment.LocalAppData, "pip", "Cache");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "payload.bin"), new byte[bytes]);
        return cache;
    }

    [Fact]
    public async Task ReportsNotPresentWhenPipWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>pip is installed as pip3 on plenty of machines, and that is still pip.</summary>
    [Fact]
    public async Task FindsPipUnderItsPip3Spelling()
    {
        _environment.WithExecutable("pip3");
        CreateCache();

        Assert.True(await CreateProvider().IsPresentAsync());
        Assert.False((await CreateProvider().PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task AsksPipWhereItsCacheIsRatherThanAssuming()
    {
        _environment.WithExecutable("pip");
        var elsewhere = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllBytes(Path.Combine(elsewhere, "payload.bin"), new byte[2048]);

        var runner = new FakeProcessRunner().Responding("cache dir", elsewhere);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("cache dir", StringComparison.Ordinal));
        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(elsewhere));
    }

    [Fact]
    public async Task AsksPipNotToColouriseThePathItIsAboutToParse()
    {
        _environment.WithExecutable("pip");
        CreateCache();

        var runner = new FakeProcessRunner();
        await CreateProvider(runner).PlanAsync();

        // Without this, pip emits ANSI escapes that would land inside the parsed path.
        Assert.Contains(runner.Invocations, i =>
            i.Arguments.Contains("--no-color", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FallsBackToTheDocumentedLocationWhenPipCannotAnswer()
    {
        _environment.WithExecutable("pip");
        var cache = CreateCache();

        var runner = new FakeProcessRunner().Responding("cache dir", string.Empty, exitCode: 1);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
    }

    [Fact]
    public async Task EvictsWithPipsOwnCommandRatherThanDeletingThePath()
    {
        _environment.WithExecutable("pip");
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        // §5.1: nothing is targeted for deletion; the tool is asked to purge.
        Assert.Empty(plan.TargetedPaths);
        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Contains("cache purge", step.Arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.6. pip.ini holds index URLs and may carry credentials for a private index, and it sits in
    /// the parent of the cache directory pip reports — so the parent is what a careless reclaim
    /// would take with it.
    /// </summary>
    [Fact]
    public async Task NeverTargetsThePipRootBecauseConfigurationLivesThere()
    {
        _environment.WithExecutable("pip");
        CreateCache();

        var pipRoot = Path.Combine(_environment.LocalAppData, "pip");
        var config = Path.Combine(pipRoot, "pip.ini");
        File.WriteAllText(config, "[global]\nindex-url = https://example.test/simple\n");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(pipRoot, plan.TargetedPaths);
        Assert.DoesNotContain(config, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == pipRoot && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == config && p.ExistedBefore);
    }

    [Fact]
    public async Task ReportsNothingToDoWhenPipIsInstalledButHasCachedNothing()
    {
        _environment.WithExecutable("pip");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task IsATierOneCacheSoItMayBePreSelected()
    {
        Assert.Equal(SafetyTier.RegenerableCache, CreateProvider().Tier);
        Assert.True(CreateProvider().Tier.IsPreSelectedByDefault());
    }

    // PIP_CACHE_DIR can move the cache between one scan and the next, so the memoised location must
    // not survive an invalidation. That invariant is not asserted here: ProviderInvalidationTests
    // holds it reflectively over every provider in the assembly, which is the stronger test — a
    // hand-written version here has to resolve a location, invalidate, and resolve again on the same
    // instance, and the obvious way of writing it constructs a second provider whose field is null
    // to begin with, so it passes with the bug present.

    /// <summary>
    /// §6.3: a cache relocated past MAX_PATH must still be measured, not silently skipped. This
    /// assertion is weaker on a machine with LongPathsEnabled set.
    /// </summary>
    [Fact]
    public async Task MeasuresACacheRelocatedBeyondMaxPath()
    {
        _environment.WithExecutable("pip");

        var deep = _temp.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        var cache = Path.Combine(deep, "pip-cache");
        Assert.True(cache.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(cache));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(cache, "payload.bin")), new byte[4096]);

        var runner = new FakeProcessRunner().Responding("cache dir", cache);
        var plan = await CreateProvider(runner).PlanAsync();

        Assert.Contains(plan.Steps.OfType<RunCommandStep>(), s => s.MeasuredPaths.Contains(cache));
        Assert.True(plan.EstimatedBytes > 0, "A cache past MAX_PATH was measured as empty.");
    }
}
