using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What each provider that names its items calls one of them. The property worth holding is that
/// the name does not move when the path does, because a keep entry that stops matching offers the
/// item again.
/// </summary>
public sealed class ItemIdentityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A profile of its own under the scratch root, standing in for one machine's layout.</summary>
    private FakeUserEnvironment Profile(string name) => new(Path.Combine(_temp.Path, name));

    private static void Populate(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[4096]);
    }

    private static async Task<DeleteStep> OnlyStep(ICleanupProvider provider) =>
        Assert.IsAssignableFrom<DeleteStep>(Assert.Single((await provider.PlanAsync()).Steps));

    [Fact]
    public async Task APlaywrightBuildKeepsItsIdentityWhenTheCacheIsMoved()
    {
        var original = Profile("original");
        Populate(Path.Combine(original.LocalAppData, "ms-playwright", "chromium-1228"));

        var moved = Profile("moved");
        var relocated = Path.Combine(_temp.Path, "elsewhere", "browsers");
        Populate(Path.Combine(relocated, "chromium-1228"));
        moved.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, relocated);

        var before = await OnlyStep(new PlaywrightBrowsersProvider(original, new FakeProcessRunner(), FakeProcessInspector.NothingRunning));
        var after = await OnlyStep(new PlaywrightBrowsersProvider(moved, new FakeProcessRunner(), FakeProcessInspector.NothingRunning));

        Assert.NotEqual(before.Path, after.Path);
        Assert.Equal(new ItemIdentity("chromium-1228", "chromium-1228"), before.Identity);
        Assert.Equal(before.Identity, after.Identity);
    }

    [Fact]
    public async Task AnAzureFunctionsReleaseKeepsItsIdentityInAnotherProfile()
    {
        var first = Profile("first");
        var second = Profile("second");

        foreach (var profile in new[] { first, second })
        {
            Populate(Path.Combine(
                profile.LocalAppData,
                AzureFunctionsToolsProvider.RootName,
                AzureFunctionsToolsProvider.ReleasesName,
                "4.18.1"));
        }

        var before = await OnlyStep(new AzureFunctionsToolsProvider(first, new FakeProcessRunner(), FakeProcessInspector.NothingRunning));
        var after = await OnlyStep(new AzureFunctionsToolsProvider(second, new FakeProcessRunner(), FakeProcessInspector.NothingRunning));

        Assert.NotEqual(before.Path, after.Path);
        Assert.Equal(new ItemIdentity("4.18.1", "Azure Functions Core Tools 4.18.1"), before.Identity);
        Assert.Equal(before.Identity, after.Identity);
    }

    [Fact]
    public async Task ASupersededSquirrelBuildKeepsItsIdentityInAnotherProfile()
    {
        var first = Profile("first");
        var second = Profile("second");

        foreach (var profile in new[] { first, second })
        {
            var application = Path.Combine(profile.LocalAppData, "Chatterbox");
            Directory.CreateDirectory(application);
            File.WriteAllBytes(Path.Combine(application, SquirrelDiscovery.UpdaterName), new byte[64]);
            Populate(Path.Combine(application, SquirrelDiscovery.PackagesDirectoryName));
            Populate(Path.Combine(application, "app-3.6.3"));
            Populate(Path.Combine(application, "app-3.6.4"));
        }

        var before = await OnlyStep(Squirrel(first));
        var after = await OnlyStep(Squirrel(second));

        Assert.NotEqual(before.Path, after.Path);
        Assert.Equal(new ItemIdentity("Chatterbox/app-3.6.3", "Chatterbox 3.6.3"), before.Identity);
        Assert.Equal(before.Identity, after.Identity);
    }

    private static SquirrelSupersededVersionProvider Squirrel(FakeUserEnvironment environment) =>
        new(
            environment,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning);
}
