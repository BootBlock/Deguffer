using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider that has to ask Windows where a tool is before it can say anything about it,
/// and the first whose tool root holds the user's game library.
///
/// <para>Two things have to hold. The install directory is <em>found</em> and never assumed, so a
/// machine that gives no answer gets a sentence rather than a guess at Program Files. And nothing
/// under <c>steamapps</c> or <c>userdata</c> is reachable by any route — which is asserted rather
/// than merely omitted, because an omission is what an over-broad rule silently stops honouring.
/// </para>
/// </summary>
public sealed class SteamCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SteamCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>Steam's folder in the profile, which is always in the same place.</summary>
    private string LocalRoot => Path.Combine(_environment.LocalAppData, "Steam");

    /// <summary>Where these tests put the install, so it is nowhere near the profile.</summary>
    private string InstallRoot => Path.Combine(_temp.Path, "games", "Steam");

    private SteamCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>A directory with a file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string path, string file = "data.bin", int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, file), new byte[bytes]);
        return path;
    }

    /// <summary>
    /// An install Steam's own record points at, carrying the client itself.
    ///
    /// <para>The recorded value is written in Steam's own form — forward slashes — because that is
    /// what the client writes, and a provider that only handled back slashes would find nothing on
    /// every real machine while every test passed.</para>
    /// </summary>
    private string RegisterInstall(string? at = null, bool withMarker = true)
    {
        var root = at ?? InstallRoot;
        Directory.CreateDirectory(root);

        if (withMarker)
        {
            File.WriteAllBytes(Path.Combine(root, "steam.exe"), new byte[64]);
        }

        _environment.WithRegistryValue(
            SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, root.Replace('\\', '/'));

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSteam()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// The profile cache alone, on a machine whose registry says nothing. The install is not guessed
    /// at, and the plan says so instead of quietly reporting a smaller number.
    /// </summary>
    [Fact]
    public async Task PlansTheProfileCacheAndSaysTheInstallWasNeverFound()
    {
        var htmlCache = Populate(Path.Combine(LocalRoot, "htmlcache"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(htmlCache, Assert.Single(plan.TargetedPaths));
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not work out where Steam is installed", StringComparison.Ordinal));
    }

    /// <summary>Both caches, once Steam's own record has been read and the client found beside it.</summary>
    [Fact]
    public async Task PlansBothCachesWhenSteamsOwnRecordNamesTheInstall()
    {
        var install = RegisterInstall();
        var htmlCache = Populate(Path.Combine(LocalRoot, "htmlcache"));
        var httpCache = Populate(Path.Combine(install, "appcache", "httpcache"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { htmlCache, httpCache }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.DoesNotContain(
            plan.Notes,
            n => n.Message.Contains("could not work out where Steam is installed", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6's negative, and the one that matters most here: the games, the half-finished download,
    /// the cloud saves and Steam's own configuration all survive a run that removed both caches, and
    /// each is asserted by name rather than covered by an assertion on the folder above it.
    /// </summary>
    [Fact]
    public async Task TheGamesTheDownloadAndTheCloudSavesAllSurvive()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(LocalRoot, "htmlcache"));
        Populate(Path.Combine(install, "appcache", "httpcache"));

        string[] mustSurvive =
        [
            LocalRoot,
            Path.Combine(LocalRoot, "cefdata"),
            Path.Combine(LocalRoot, "widevine"),
            install,
            Path.Combine(install, "appcache"),
            Path.Combine(install, "appcache", "librarycache"),
            Path.Combine(install, "steamapps"),
            Path.Combine(install, "steamapps", "common"),
            Path.Combine(install, "steamapps", "downloading"),
            Path.Combine(install, "steamapps", "workshop"),
            Path.Combine(install, "userdata"),
            Path.Combine(install, "config"),
        ];

        foreach (var directory in mustSurvive)
        {
            Populate(directory);
        }

        // Files, not directories. A child set classifies directories, so these are only ever
        // asserted because the provider names them — the NVIDIA 'accounts' lesson.
        var appInfo = Path.Combine(install, "appcache", "appinfo.vdf");
        var packageInfo = Path.Combine(install, "appcache", "packageinfo.vdf");
        File.WriteAllBytes(appInfo, new byte[128]);
        File.WriteAllBytes(packageInfo, new byte[128]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in mustSurvive.Concat([appInfo, packageInfo]))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(mustSurvive, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.True(File.Exists(appInfo));
        Assert.True(File.Exists(packageInfo));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe. Neither root is ever
    /// enumerated, so an unnamed neighbour is unreachable by construction — this is the assertion
    /// that the construction is what it claims to be.
    /// </summary>
    [Theory]
    [InlineData(true, "logs")]
    [InlineData(true, "something-unrecognised")]
    [InlineData(false, "package")]
    [InlineData(false, "something-unrecognised")]
    public async Task AnUnrecognisedNeighbourIsNeverATarget(bool inProfile, string name)
    {
        var install = RegisterInstall();
        Populate(Path.Combine(LocalRoot, "htmlcache"));
        Populate(Path.Combine(install, "appcache", "httpcache"));

        var neighbour = Populate(Path.Combine(inProfile ? LocalRoot : install, name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(neighbour, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(neighbour), $"{neighbour} was removed");
    }

    /// <summary>
    /// A record pointing somewhere the Steam program is not. The path is declined rather than
    /// treated as an install, and the user is told which path it was — telling somebody Deguffer
    /// found nothing would send them looking for a record they do have.
    /// </summary>
    [Fact]
    public async Task ARecordedInstallWithoutTheSteamProgramIsDeclinedByName()
    {
        var elsewhere = RegisterInstall(Path.Combine(_temp.Path, "not-steam"), withMarker: false);
        Populate(Path.Combine(elsewhere, "appcache", "httpcache"));
        Directory.CreateDirectory(LocalRoot);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(elsewhere, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Steam is in the profile, nothing points at the install, and there is no cache to offer. The
    /// row must not read "Already clear": a whole cache directory was never looked at.
    /// </summary>
    [Fact]
    public async Task ASteamWhoseInstallCannotBeFoundIsPresentAndUnexamined()
    {
        Directory.CreateDirectory(LocalRoot);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// A cache moved onto another drive with a link. Deguffer removes nothing through it and says
    /// so, rather than deleting the far side of a redirection nobody classified.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsLeftAloneAndReported()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateSymbolicLink(Path.Combine(LocalRoot, "htmlcache"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.True(Directory.Exists(outside));
    }

    /// <summary>
    /// The container between the install root and the cache. It is left standing while something
    /// inside it is removed, which is the one case where "we did not recognise that" would be an
    /// actively false thing to say — so it carries its own reason and is asserted individually.
    /// </summary>
    [Fact]
    public async Task AJunctionedAppCacheContainerIsNeverLookedThrough()
    {
        var install = RegisterInstall();
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere", "httpcache"));
        Directory.CreateSymbolicLink(
            Path.Combine(install, "appcache"), Path.Combine(_temp.Path, "elsewhere"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(outside));
    }

    /// <summary>
    /// G4: the registry is asked once per planning pass, however many questions are put to the
    /// provider — and asked again after an invalidation, so a Steam installed while the app was
    /// open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheRegistryIsReadOncePerPassAndAgainAfterInvalidation()
    {
        RegisterInstall();
        Populate(Path.Combine(LocalRoot, "htmlcache"));

        var provider = CreateProvider();

        await provider.IsPresentAsync();
        await provider.PlanAsync();
        _ = provider.ToolRoots;

        Assert.Equal(1, _environment.RegistryReads);

        provider.InvalidateCaches();
        await provider.IsPresentAsync();

        Assert.Equal(2, _environment.RegistryReads);
    }

    /// <summary>
    /// The whole table, read back. Two roots and exactly two paths under them, so adding a third
    /// location — <c>steamapps</c> is the one that would matter — fails here rather than in a
    /// deletion.
    ///
    /// <para>Read from the declaration rather than from a plan, so it holds on a machine with no
    /// cache on disk at all, where a plan-based assertion would pass with nothing in it.</para>
    /// </summary>
    [Fact]
    public void TheDeclarationNamesTheTwoCachesAndNothingElse()
    {
        var install = RegisterInstall();
        var provider = CreateProvider();

        Assert.Equal(new[] { LocalRoot, install }, provider.Roots.Select(r => r.Path));

        Assert.Equal(
            new[]
            {
                Path.Combine(LocalRoot, "htmlcache"),
                Path.Combine(install, "appcache", "httpcache"),
            },
            provider.Roots.SelectMany(
                root => root.Locations.Select(l => Path.Combine(root.Path, l.RelativePath))));
    }
}
