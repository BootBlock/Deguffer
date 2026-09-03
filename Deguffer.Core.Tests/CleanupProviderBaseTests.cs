using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The shared parts of a provider, driven through <see cref="GradleCacheProvider"/> because it is
/// the reference §5.2 case rather than because the rules here are Gradle's.
///
/// Child enumeration used to be copied into each provider that classifies children, and the
/// reparse-point skip inside it — the difference between deleting a cache and deleting whatever a
/// junction points at — was carried by every copy and tested by none.
/// <see cref="DirectoryRemoverTests.DeletesAJunctionWithoutFollowingItIntoTheTargetTree"/> covers
/// the removal end. This covers the planning end, where a junction wearing a recognised name must
/// never become a target in the first place.
/// </summary>
public sealed class CleanupProviderBaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public CleanupProviderBaseTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task AJunctionWearingARecognisedNameIsNeverATarget()
    {
        var root = Path.Combine(_environment.UserProfile, ".gradle");
        Directory.CreateDirectory(root);

        var outside = Path.Combine(_temp.Path, "precious");
        Directory.CreateDirectory(outside);
        var bystander = Path.Combine(outside, "irreplaceable.bin");
        File.WriteAllBytes(bystander, new byte[4096]);

        // "caches" is a name the provider recognises. The reparse point is the only thing standing
        // between this plan and a deletion that escapes the profile entirely.
        var junction = Path.Combine(root, "caches");
        Directory.CreateSymbolicLink(junction, outside);

        var provider = new GradleCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(outside), "planning followed a junction out of the tool root");
        Assert.True(File.Exists(bystander), "a file outside the tool root was destroyed");

        // Skipping it silently would leave a plan that disagrees with the folder the user can see:
        // a child named 'caches' is there, and nothing said why it was not offered.
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("caches", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));
    }

    /// <summary>
    /// The guard is stamped onto whatever a provider hands back, rather than by each provider.
    ///
    /// There are fifty places a provider constructs a plan, and a plan that reached the executor
    /// without <see cref="CleanupPlan.Keep"/> would be carried out as though no guard existed. That
    /// is a deletion the preview said would not happen, so it must not be something a new provider
    /// can forget.
    /// </summary>
    [Fact]
    public async Task EveryPlanCarriesTheGuardItWasBuiltUnder()
    {
        var caches = Path.Combine(_environment.UserProfile, ".gradle", "caches");
        Directory.CreateDirectory(caches);
        TempDirectory.Age(_temp.CreateFile(4096, ".gradle", "caches", "a.bin"), TimeSpan.FromDays(30));

        var keep = MinimumAge.WithinHours(8, DateTime.UtcNow);
        var plan = await Provider().PlanAsync(keep);

        Assert.Equal(keep, plan.Keep);
        Assert.Contains(plan.Notes, n => n.Message.Contains("8 hours", StringComparison.Ordinal));

        // And an unguarded plan says nothing about a setting the user did not turn on.
        var unguarded = await Provider().PlanAsync();

        Assert.False(unguarded.Keep.IsOn);
        Assert.DoesNotContain(unguarded.Notes, n => n.Message.Contains("hours", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.1 keeps a tool's own eviction command as the preferred route, and that command decides for
    /// itself what it removes — so the guard does not reach it. The alternative is to stop using the
    /// command, which would replace the tool's knowledge of its own cache with ours: NuGet's own
    /// clear reached two locations that were not under <c>.nuget</c> at all.
    ///
    /// <para>Saying so is then the whole of what can be done, and it has to be a warning rather than
    /// a remark. A guard whose gap is unstated is worse than no guard.</para>
    /// </summary>
    [Fact]
    public async Task WarnsThatAToolsOwnCleanIsNotCoveredByTheGuard()
    {
        var cache = Path.Combine(_environment.LocalAppData, "npm-cache");
        Directory.CreateDirectory(Path.Combine(cache, "_cacache", "content-v2"));
        File.WriteAllBytes(Path.Combine(cache, "_cacache", "content-v2", "blob"), new byte[4096]);

        _environment.WithExecutable("npm");
        var runner = new FakeProcessRunner().Responding("config get cache", cache);
        var provider = new NpmCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);
        var plan = await provider.PlanAsync(MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.Contains(plan.Steps, s => s is RunCommandStep);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning &&
            n.Message.Contains("its own tool", StringComparison.Ordinal));
    }

    private GradleCacheProvider Provider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
}
