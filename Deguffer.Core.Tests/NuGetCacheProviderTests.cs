using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1's headline case: NuGet's own clear reached four locations, two outside <c>.nuget</c>.
/// A path-based cleaner would have missed ~3 GB, so the plan defers to the tool.
/// </summary>
public sealed class NuGetCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public NuGetCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReportsNotPresentWithoutTheDotnetSdk()
    {
        var provider = new NuGetCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheEvictionCommandRatherThanDeletingAnyPath()
    {
        var (plan, _) = await PlanWithLocals();

        Assert.Empty(plan.TargetedPaths);

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal("nuget locals all --clear", command.Arguments);
    }

    [Fact]
    public async Task MeasuresEveryLocationNuGetReportsIncludingThoseOutsideDotNuget()
    {
        var (plan, locations) = await PlanWithLocals();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.Equal(locations.Order(StringComparer.OrdinalIgnoreCase), command.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(command.MeasuredPaths, p => !p.Contains(".nuget", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverTargetsTheDotNugetRootDirectory()
    {
        var (plan, _) = await PlanWithLocals();
        var root = Path.Combine(_environment.UserProfile, ".nuget");

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProbesBothNuGetConfigLocationsBecauseItsLocationCannotBeAssumed()
    {
        var (plan, _) = await PlanWithLocals();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(_environment.RoamingAppData, "NuGet", "NuGet.Config"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(_environment.UserProfile, ".nuget", "NuGet.Config"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParsesWindowsDriveLettersWithoutMistakingThemForTheFieldSeparator()
    {
        // "global-packages: C:\..." has two colons. Splitting on the wrong one yields "\..." ,
        // which is still rooted and would silently measure the wrong volume.
        var (plan, locations) = await PlanWithLocals();
        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));

        Assert.All(command.MeasuredPaths, p => Assert.Equal(Path.GetPathRoot(locations[0]), Path.GetPathRoot(p)));
    }

    private async Task<(CleanupPlan Plan, string[] Locations)> PlanWithLocals()
    {
        // Deliberately mirrors the audit: two locations under .nuget, two well outside it.
        string[] locations =
        [
            _temp.CreateDirectory("profile", ".nuget", "packages"),
            _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "v3-cache"),
            _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "plugins-cache"),
            _temp.CreateDirectory("scratch", "NuGetScratch"),
        ];

        foreach (var location in locations)
        {
            File.WriteAllBytes(Path.Combine(location, "payload.bin"), new byte[1024]);
        }

        var listing = string.Join(
            "\n",
            $"http-cache: {locations[1]}",
            $"global-packages: {locations[0]}",
            $"temp: {locations[3]}",
            $"plugins-cache: {locations[2]}");

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding("locals all --list", listing);

        var plan = await new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning).PlanAsync();
        return (plan, locations);
    }
}
