using Deguffer.Core.Providers;
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
    }
}
