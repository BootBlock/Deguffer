using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
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
    /// The embedded browser's folder, on a machine whose registry says nothing. It is a browser
    /// user-data folder with the client's sign-in in it, so this row never takes it, whole or in
    /// part, and the install is not guessed at: the plan says so rather than reporting the row clear.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheBrowserFolderAndSaysTheInstallWasNeverFound()
    {
        Populate(Path.Combine(LocalRoot, "htmlcache", "Default", "Code Cache"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not work out where Steam is installed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The HTTP cache alone, once Steam's own record has been read and the client found beside it,
    /// with the browser's folder on disk as well.
    /// </summary>
    [Fact]
    public async Task PlansOnlyTheHttpCacheWhenSteamsOwnRecordNamesTheInstall()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(LocalRoot, "htmlcache"));
        var httpCache = Populate(Path.Combine(install, "appcache", "httpcache"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(httpCache, Assert.Single(plan.TargetedPaths));

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.DoesNotContain(
            plan.Notes,
            n => n.Message.Contains("could not work out where Steam is installed", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6's negative, and the one that matters most here: the games, the half-finished download,
    /// the cloud saves and Steam's own configuration all survive a run that removed the cache, and
    /// each is asserted by name rather than covered by an assertion on the folder above it. Steam's
    /// folder in the profile, the browser's sign-in included, is checked on disk: this row does not
    /// reach into it, so its plan has nothing to say about it.
    /// </summary>
    [Fact]
    public async Task TheGamesTheDownloadAndTheCloudSavesAllSurvive()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(install, "appcache", "httpcache"));

        string[] profileDirectories =
        [
            LocalRoot,
            Path.Combine(LocalRoot, "cefdata"),
            Path.Combine(LocalRoot, "widevine"),
            Path.Combine(LocalRoot, "htmlcache"),
            Path.Combine(LocalRoot, "htmlcache", "Default", "Code Cache"),
        ];

        string[] profileFiles =
        [
            Path.Combine(LocalRoot, "local.vdf"),
            Path.Combine(LocalRoot, "htmlcache", "Local State"),
            Path.Combine(LocalRoot, "htmlcache", "Default", "Login Data"),
        ];

        foreach (var directory in profileDirectories)
        {
            Populate(directory);
        }

        foreach (var file in profileFiles)
        {
            File.WriteAllBytes(file, new byte[128]);
        }

        string[] mustSurvive =
        [
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
        string[] files =
        [
            Path.Combine(install, "appcache", "appinfo.vdf"),
            Path.Combine(install, "appcache", "packageinfo.vdf"),
        ];

        foreach (var file in files)
        {
            File.WriteAllBytes(file, new byte[128]);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in mustSurvive.Concat(files))
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(mustSurvive.Concat(profileDirectories), d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.All(files.Concat(profileFiles), f => Assert.True(File.Exists(f), $"{f} was removed"));
        Assert.DoesNotContain(plan.TargetedPaths, p => p.StartsWith(LocalRoot, StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe. The install directory is
    /// never enumerated and Steam's folder in the profile is not this row's at all, so an unnamed
    /// neighbour in either is unreachable by construction — this is the assertion that the
    /// construction is what it claims to be.
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
    /// A recorded install Windows will not describe is named as one Deguffer could not reach. The
    /// two-state probe read it as no record at all, and with no Steam folder in the profile the row
    /// was not drawn, about a directory Steam's own record names and the game library sits in.
    /// Explore refuses all of it, because nothing established what is in there.
    /// </summary>
    [Fact]
    public async Task ARecordedInstallWindowsWillNotDescribeIsSaidToBeUnreachedAndRefusedWhole()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(install, "steamapps", "common", "SomeGame"));

        using var denied = DeniedDirectory.WithUnreadableAttributes(install);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(install, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("could not work out", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("is not there", StringComparison.Ordinal));

        var declared = Assert.Single(
            provider.ToolRoots, r => r.Path.Equals(install, StringComparison.OrdinalIgnoreCase));
        Assert.False(declared.RecognisesFolder("steamapps"));
        Assert.False(declared.RecognisesFolder("appcache"));
    }

    /// <summary>
    /// A Steam folder in the profile that Windows will not describe is no evidence that Steam was
    /// never installed, so the sentence about the install it could not find is still said.
    /// </summary>
    [Fact]
    public async Task AProfileFolderWindowsWillNotDescribeStillOwesTheSentenceAboutTheInstall()
    {
        Populate(Path.Combine(LocalRoot, "htmlcache"));

        using var denied = DeniedDirectory.WithUnreadableAttributes(LocalRoot);

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not work out where Steam is installed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cache moved onto another drive with a link. Deguffer removes nothing through it and says
    /// so, rather than deleting the far side of a redirection nobody classified.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsLeftAloneAndReported()
    {
        var install = RegisterInstall();
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        Directory.CreateDirectory(Path.Combine(install, "appcache"));
        SymbolicLink.ToDirectory(Path.Combine(install, "appcache", "httpcache"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        // Executed, or the assertion below could not fail: nothing else in this test deletes
        // anything. Run the plan and the far side of the link is either still there or it is not.
        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
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
        SymbolicLink.ToDirectory(
            Path.Combine(install, "appcache"), Path.Combine(_temp.Path, "elsewhere"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
    }

    /// <summary>
    /// G4: the registry is asked once per planning pass, however many questions are put to the
    /// provider — and asked again after an invalidation, so a Steam installed while the app was
    /// open is seen on the next preview.
    /// </summary>
    [Fact]
    public async Task TheRegistryIsReadOncePerPassAndAgainAfterInvalidation()
    {
        var install = RegisterInstall();
        Populate(Path.Combine(install, "appcache", "httpcache"));

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
    /// The whole table, read back. One root and exactly one path under it, so adding a second
    /// location — <c>steamapps</c> and the browser's <c>htmlcache</c> are the ones that would
    /// matter — fails here rather than in a deletion.
    ///
    /// <para>Read from the declaration rather than from a plan, so it holds on a machine with no
    /// cache on disk at all, where a plan-based assertion would pass with nothing in it.</para>
    /// </summary>
    [Fact]
    public void TheDeclarationNamesTheHttpCacheAndNothingElse()
    {
        var install = RegisterInstall();
        var provider = CreateProvider();

        Assert.Equal(new[] { install }, provider.Roots.Select(r => r.Path));

        Assert.Equal(
            new[] { Path.Combine(install, "appcache", "httpcache") },
            provider.Roots.SelectMany(
                root => root.Locations.Select(l => Path.Combine(root.Path, l.RelativePath))));
    }

    /// <summary>
    /// Without an install there is nothing for this row to name, however much is in Steam's folder
    /// in the profile.
    /// </summary>
    [Fact]
    public void WithoutAnInstallTheDeclarationNamesNothing()
    {
        Populate(Path.Combine(LocalRoot, "htmlcache"));

        Assert.Empty(CreateProvider().Roots);
    }
}
