using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

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

    [Fact]
    public async Task ReResolvesTheLocalsAfterInvalidationBecauseTheyCanMove()
    {
        // NuGet reports a list, so both sides use two locations: a resolver that rebuilt only the
        // first entry, or kept a stale one alongside the new, would pass a single-path test.
        // None of the four is a DefaultLocals() entry, so the first assertion proves NuGet was
        // asked rather than passing on the documented fallback by coincidence.
        string[] before_ = [_temp.CreateDirectory("configured", "packages"), _temp.CreateDirectory("configured", "http")];
        string[] after_ = [_temp.CreateDirectory("relocated", "packages"), _temp.CreateDirectory("relocated", "http")];

        foreach (var location in before_.Concat(after_))
        {
            File.WriteAllBytes(Path.Combine(location, "payload.bin"), new byte[1024]);
        }

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(
            "locals all --list", $"global-packages: {before_[0]}\nhttp-cache: {before_[1]}");
        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        var planned = await provider.PlanAsync();
        var step = Assert.IsType<RunCommandStep>(Assert.Single(planned.Steps));
        Assert.All(before_, p => Assert.Contains(p, step.MeasuredPaths));

        // NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH both moved between scans; the planner
        // invalidates every provider before replanning.
        runner.Responding("locals all --list", $"global-packages: {after_[0]}\nhttp-cache: {after_[1]}");
        provider.InvalidateCaches();

        var replanned = await provider.PlanAsync();
        var replannedStep = Assert.IsType<RunCommandStep>(Assert.Single(replanned.Steps));

        Assert.All(after_, p => Assert.Contains(p, replannedStep.MeasuredPaths));
        Assert.All(before_, p => Assert.DoesNotContain(p, replannedStep.MeasuredPaths));
    }

    /// <summary>
    /// A location NuGet names and Windows will not describe is reported as one Deguffer could not
    /// reach. "None of its NuGet cache locations exist yet" was said about a packages folder holding
    /// a package, because the two-state probe read the refusal as absence.
    /// </summary>
    [Fact]
    public async Task ALocationWindowsWillNotDescribeIsSaidToBeUnreachedRatherThanMissing()
    {
        var packages = _temp.CreateDirectory("profile", ".nuget", "packages");
        File.WriteAllBytes(Path.Combine(packages, "payload.bin"), new byte[1024]);

        using var denied = DeniedDirectory.WithUnreadableAttributes(packages);

        var plan = await PlanReporting($"global-packages: {packages}");

        Assert.True(plan.HasUnreadableRoot);
        Assert.Empty(plan.Steps);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(packages, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("exist yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// NuGet's command clears every location it named, the refused one included, so the one the
    /// figure leaves out is named beside it rather than dropped.
    /// </summary>
    [Fact]
    public async Task ALocationWindowsWillNotDescribeIsNamedBesideTheOnesMeasured()
    {
        var packages = _temp.CreateDirectory("profile", ".nuget", "packages");
        var http = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "v3-cache");

        using var denied = DeniedDirectory.WithUnreadableAttributes(http);

        var plan = await PlanReporting($"global-packages: {packages}\nhttp-cache: {http}");

        var step = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal([packages], step.MeasuredPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(http, StringComparison.Ordinal));
    }

    private Task<CleanupPlan> PlanReporting(string listing)
    {
        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding("locals all --list", listing);

        return new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning).PlanAsync();
    }

    /// <summary>
    /// NuGet's command clears its scratch folder, so the temporary-folder row must leave it to this
    /// one — and only it: the caches NuGet keeps elsewhere are no entry of a temporary folder.
    /// </summary>
    [Fact]
    public async Task ClaimsItsScratchFolderInATemporaryFolderAndNothingElse()
    {
        var packages = _temp.CreateDirectory("profile", ".nuget", "packages");
        var scratch = _temp.CreateDirectory("temp", "NuGetScratch");
        var temporary = Path.GetDirectoryName(scratch)!;

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(
            "locals all --list", $"global-packages: {packages}\\\ntemp: {scratch}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.Equal([scratch], await provider.ClaimedEntriesAsync([temporary]));
        Assert.Empty(await provider.ClaimedEntriesAsync([_temp.CreateDirectory("elsewhere")]));
    }

    /// <summary>
    /// NuGet names its scratch folder whether or not it exists, and a claim on nothing would have the
    /// temporary-folder row say it left an entry out.
    /// </summary>
    [Fact]
    public async Task ClaimsNoScratchFolderThatIsNotThere()
    {
        var temporary = _temp.CreateDirectory("temp");
        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(
            "locals all --list", $"temp: {Path.Combine(temporary, "NuGetScratch")}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.Empty(await provider.ClaimedEntriesAsync([temporary]));
    }

    /// <summary>
    /// NuGet reports the temporary folder as its own process sees it, which on a profile with a long
    /// folder name is the 8.3 alias. The claim is still made, and in the caller's own form, which is
    /// the form the temporary-folder row's removal compares against.
    ///
    /// <para>On a volume that creates no 8.3 aliases the fixture falls back to the long form, and the
    /// test is then the one above. The folder name is longer than eight characters so that every
    /// volume that does create them gives it one.</para>
    /// </summary>
    [Fact]
    public async Task ClaimsItsScratchFolderWhenNuGetReportsTheShortForm()
    {
        var scratch = _temp.CreateDirectory("temporary-folder", "NuGetScratch");
        var temporary = Path.GetDirectoryName(scratch)!;
        var asReported = ShortPath.Of(temporary) ?? temporary;

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(
            "locals all --list", $"temp: {Path.Combine(asReported, "NuGetScratch")}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning);

        Assert.Equal([scratch], await provider.ClaimedEntriesAsync([temporary]));
    }

    /// <summary>Without the SDK nothing offers the scratch folder here, so the temporary-folder row keeps it.</summary>
    [Fact]
    public async Task ClaimsNothingWithoutTheDotnetSdk()
    {
        var scratch = _temp.CreateDirectory("temp", "NuGetScratch");
        var provider = new NuGetCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.Empty(await provider.ClaimedEntriesAsync([Path.GetDirectoryName(scratch)!]));
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
