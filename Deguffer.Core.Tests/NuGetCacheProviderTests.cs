using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
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

    /// <summary>The dotnet the fake environment resolves, so a test names the program a step must run.</summary>
    private string Dotnet => _environment.FindExecutable("dotnet")!;

    [Fact]
    public async Task ReportsNotPresentWithoutTheDotnetSdk()
    {
        var provider = new NuGetCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheEvictionCommandRatherThanDeletingAnyPath()
    {
        var (plan, _) = await PlanWithLocals();

        Assert.Empty(plan.TargetedPaths);

        var command = Assert.IsType<RunCommandStep>(Assert.Single(plan.Steps));
        Assert.Equal(Dotnet, command.FileName);
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

    /// <summary>
    /// §5.6 for both folders NuGet's command clears inside. Each child that is not one of its caches
    /// is a sibling of one the command empties, which is where an over-broad rule takes one with the
    /// other, so the run has to prove it survived. The caches themselves are not named: the command
    /// is meant to empty them.
    /// </summary>
    [Fact]
    public async Task ProtectsBothNuGetFoldersAndEverythingInThemThatIsNotACache()
    {
        var local = Path.Combine(_environment.LocalAppData, "NuGet");
        var profile = Path.Combine(_environment.UserProfile, ".nuget");

        var legacy = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "Cache");
        _temp.CreateDirectory("profile", ".nuget", "plugins", "netcore", "CredentialProvider");
        var unknown = _temp.CreateDirectory("profile", ".nuget", "something-nuget-added-later");

        var linked = Path.Combine(local, "linked");
        SymbolicLink.ToDirectory(linked, _temp.CreateDirectory("far-side"));

        var (plan, locations) = await PlanWithLocals();

        string[] spared = [local, legacy, linked, Path.Combine(profile, "plugins"), unknown];

        Assert.All(spared, path => Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present));

        Assert.All(locations, cache => Assert.DoesNotContain(plan.ProtectedPaths, p =>
            p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// NUGET_HTTP_CACHE_PATH can put the cache in any folder, and the command then empties it. A
    /// folder holding it is not asserted unchanged, or every successful run would fail verification.
    /// NuGet reports the location as its own process sees it, so the short form is the one used.
    ///
    /// <para>On a volume that creates no 8.3 aliases the fixture falls back to the long form. The
    /// folder name is longer than eight characters so that every volume that does create them gives
    /// it one.</para>
    /// </summary>
    [Fact]
    public async Task DoesNotProtectAFolderHoldingACacheNuGetWasPointedAt()
    {
        var holder = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "relocated-caches");
        _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "relocated-caches", "http");
        var beside = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "something-else");

        var asReported = Path.Combine(ShortPath.Of(holder) ?? holder, "http");

        var plan = await PlanReporting($"http-cache: {asReported}");

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(holder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(beside, StringComparison.OrdinalIgnoreCase));
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
            Dotnet, "locals all --list", $"global-packages: {before_[0]}\nhttp-cache: {before_[1]}");
        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        var planned = await provider.PlanAsync();
        var step = Assert.IsType<RunCommandStep>(Assert.Single(planned.Steps));
        Assert.All(before_, p => Assert.Contains(p, step.MeasuredPaths));

        // NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH both moved between scans; the planner
        // invalidates every provider before replanning.
        runner.Responding(Dotnet, "locals all --list", $"global-packages: {after_[0]}\nhttp-cache: {after_[1]}");
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

    /// <summary>
    /// The case above with NuGet reporting the cache through another mount of the profile's volume:
    /// the folder holding it is still emptied by the command, so it is not asserted unchanged. A
    /// folder beside it is still protected (§5.6). The mount is a folder of its own here, standing in
    /// for the same folder reached the other way.
    /// </summary>
    [Fact]
    public async Task DoesNotProtectAFolderHoldingACacheNuGetReportsThroughAnotherMount()
    {
        var holder = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "relocated-caches");
        _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "relocated-caches", "http");
        var beside = _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "something-else");
        using var mirror = new TempDirectory();
        var volumes = new FakeVolumeInventory().With(_environment.UserProfile + @"\", alsoMountedAt: [mirror.Path + @"\"]);
        var reported = mirror.CreateDirectory("AppData", "Local", "NuGet", "relocated-caches", "http");

        var plan = await PlanReporting($"http-cache: {reported}", volumes);

        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(holder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(beside, StringComparison.OrdinalIgnoreCase));
    }

    private Task<CleanupPlan> PlanReporting(string listing, IVolumeInventory? volumes = null)
    {
        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(Dotnet, "locals all --list", listing);

        return new NuGetCacheProvider(
            _environment, runner, FakeProcessInspector.NothingRunning, volumes: volumes ?? new FakeVolumeInventory()).PlanAsync();
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
            Dotnet, "locals all --list", $"global-packages: {packages}\\\ntemp: {scratch}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

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
            Dotnet, "locals all --list", $"temp: {Path.Combine(temporary, "NuGetScratch")}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

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
            Dotnet, "locals all --list", $"temp: {Path.Combine(asReported, "NuGetScratch")}\\");

        var provider = new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        Assert.Equal([scratch], await provider.ClaimedEntriesAsync([temporary]));
    }

    /// <summary>
    /// NuGet reporting its scratch folder through <c>S:</c>, a letter <c>subst</c> made for the
    /// temporary folder, still names an entry of that folder, which is claimed as the temporary folder
    /// names it. A scratch folder one level further down reached the same way is not one of its entries.
    /// </summary>
    [Theory]
    [InlineData(@"S:\NuGetScratch", true)]
    [InlineData(@"S:\nested\NuGetScratch", false)]
    public async Task ClaimsItsScratchFolderWhenNuGetReportsItThroughASubstitutedLetter(string reported, bool claimed)
    {
        var scratch = _temp.CreateDirectory("temp", "NuGetScratch");
        var temporary = Path.GetDirectoryName(scratch)!;

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(Dotnet, "locals all --list", $"temp: {reported}\\");

        var provider = new NuGetCacheProvider(
            _environment, runner, FakeProcessInspector.NothingRunning,
            volumes: new FakeVolumeInventory().Substituting(@"S:\", temporary));

        Assert.Equal(claimed ? [scratch] : [], await provider.ClaimedEntriesAsync([temporary]));
    }

    /// <summary>Without the SDK nothing offers the scratch folder here, so the temporary-folder row keeps it.</summary>
    [Fact]
    public async Task ClaimsNothingWithoutTheDotnetSdk()
    {
        var scratch = _temp.CreateDirectory("temp", "NuGetScratch");
        var provider = new NuGetCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        Assert.Empty(await provider.ClaimedEntriesAsync([Path.GetDirectoryName(scratch)!]));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheDefaultLocalsWhenNuGetDeclinesToList()
    {
        string[] defaults =
        [
            _temp.CreateDirectory("profile", ".nuget", "packages"),
            _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "v3-cache"),
            _temp.CreateDirectory("profile", "AppData", "Local", "NuGet", "plugins-cache"),
            Path.Combine(_environment.TempPath, "NuGetScratch"),
        ];

        foreach (var location in defaults)
        {
            Directory.CreateDirectory(location);
            File.WriteAllBytes(Path.Combine(location, "payload.bin"), new byte[1024]);
        }

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(Dotnet, "locals all --list", string.Empty, exitCode: 1);
        var provider = new NuGetCacheProvider(
            _environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(provider));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryLocalNuGetReportsIncludingOnesAwayFromTheDefaults()
    {
        var relocated = _temp.CreateDirectory("elsewhere", "nuget-packages");
        var scratch = _temp.CreateDirectory("scratch", "NuGetScratch");
        File.WriteAllBytes(Path.Combine(relocated, "payload.bin"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(scratch, "payload.bin"), new byte[1024]);

        _environment.WithExecutable("dotnet");
        var runner = new FakeProcessRunner().Responding(
            Dotnet, "locals all --list", $"global-packages: {relocated}\\\ntemp: {scratch}\n");
        var provider = new NuGetCacheProvider(
            _environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory());

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(provider));
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
        var runner = new FakeProcessRunner().Responding(Dotnet, "locals all --list", listing);

        var plan = await new NuGetCacheProvider(_environment, runner, FakeProcessInspector.NothingRunning, volumes: new FakeVolumeInventory()).PlanAsync();
        return (plan, locations);
    }
}
