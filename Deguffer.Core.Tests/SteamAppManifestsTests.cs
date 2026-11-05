using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The one reader of Steam's game manifests that the artwork and shader rows share for a planning
/// pass. Each library's records are listed once and each manifest is read once between the rows,
/// "not installed" is still a claim about every library, and each row names only the libraries that
/// decided something about its own games.
/// </summary>
public sealed class SteamAppManifestsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SteamAppManifestsTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string InstallRoot => Path.Combine(_temp.Path, "games", "Steam");

    /// <summary>A second library, on what would be another drive.</summary>
    private string SecondLibrary => Path.Combine(_temp.Path, "second-drive", "SteamLibrary");

    /// <summary>A library on a drive that is not connected: listed by Steam, and not there.</summary>
    private string UnpluggedLibrary => Path.Combine(_temp.Path, "unplugged-drive", "SteamLibrary");

    private SteamShaderCacheProvider ShaderRow(SteamDiscovery steam) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, discovery: steam);

    private SteamLibraryArtworkProvider ArtworkRow(SteamDiscovery steam) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, discovery: steam);

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    /// <summary>The install, recorded as Steam records it, with the client beside it.</summary>
    private void RegisterInstall()
    {
        WriteFile(Path.Combine(InstallRoot, "steam.exe"));

        _environment.WithRegistryValue(
            SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, InstallRoot.Replace('\\', '/'));
    }

    /// <summary>Steam's list of libraries, naming the install and <paramref name="others"/>.</summary>
    private void WriteLibraryList(params string[] others)
    {
        var blocks = new[] { InstallRoot }.Concat(others).Select((library, index) =>
            $$"""    "{{index}}" { "path" "{{library.Replace(@"\", @"\\")}}" }""");

        var list = Path.Combine(InstallRoot, "steamapps", SteamLibraryFolders.ListFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        File.WriteAllText(list, $"\"libraryfolders\"\n{{\n{string.Join("\n", blocks)}\n}}\n");
    }

    private static string Manifest(string library, string appId, string name)
    {
        var path = Path.Combine(library, SteamAppManifests.RelativePath(appId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            "AppState"
            {
                "appid"		"{{appId}}"
                "name"		"{{name}}"
                "installdir"		"{{name}}"
            }
            """);
        return path;
    }

    private static string ShaderCacheFor(string library, string appId) =>
        Path.GetDirectoryName(WriteFile(Path.Combine(library, "steamapps", "shadercache", appId, "fozpipelinesv6", "data.bin")))!;

    private string ArtworkFor(string appId) =>
        Path.GetDirectoryName(WriteFile(Path.Combine(InstallRoot, "appcache", "librarycache", appId, "header.jpg")))!;

    /// <summary>The game a plan's step for <paramref name="appId"/> was described as.</summary>
    private static SteamApp GameIn(CleanupPlan plan, string appId)
    {
        var step = Assert.Single(plan.Steps.OfType<DeleteStep>(), s => s.Identity?.Key == appId);
        var name = step.Identity!.Name == $"Steam app {appId}" ? null : step.Identity.Name;

        return new SteamApp(appId, name, step.Facets.SingleOrDefault(f => f.Label == "Installed")?.Value switch
        {
            "Yes" => true,
            "No" => false,
            _ => null,
        });
    }

    /// <summary>
    /// The two rows plan at the same time from one discovery. Between them each library is listed
    /// once, and each manifest of a game with both a shader cache and artwork is read once, rather
    /// than probed in every library and read by each row. Nothing about what is removed changes: the
    /// manifest of each game whose shader cache is offered survives the run.
    /// </summary>
    [Fact]
    public async Task BothRowsInOnePassListEachLibraryOnceAndReadEachManifestOnce()
    {
        RegisterInstall();
        WriteLibraryList(SecondLibrary);

        var manifests = new[]
        {
            Manifest(InstallRoot, "440", "Team Fortress 2"),
            Manifest(SecondLibrary, "570", "Dota 2"),
        };

        foreach (var (library, appId) in new[] { (InstallRoot, "440"), (SecondLibrary, "570") })
        {
            ShaderCacheFor(library, appId);
            ArtworkFor(appId);
        }

        // Owned and never installed: found in no library, at the cost of no probe of its own.
        foreach (var appId in Enumerable.Range(1000, 50).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            ArtworkFor(appId);
        }

        var steam = new SteamDiscovery(_environment);
        var shaderRow = ShaderRow(steam);
        var artworkRow = ArtworkRow(steam);

        var plans = await Task.WhenAll(
            Task.Run(() => shaderRow.PlanAsync()),
            Task.Run(() => artworkRow.PlanAsync()));

        var reader = steam.Manifests!;
        Assert.Equal(2, reader.Listings);
        Assert.Equal(2, reader.Reads);

        foreach (var plan in plans)
        {
            Assert.Equal(new SteamApp("440", "Team Fortress 2", true), GameIn(plan, "440"));
            Assert.Equal(new SteamApp("570", "Dota 2", true), GameIn(plan, "570"));
        }

        Assert.Equal(new SteamApp("1000", null, false), GameIn(plans[1], "1000"));

        var shaderPlan = plans[0];

        foreach (var manifest in manifests)
        {
            Assert.Contains(shaderPlan.ProtectedPaths, p => p.Path.Equals(manifest, StringComparison.OrdinalIgnoreCase));
        }

        Assert.True((await shaderRow.ExecuteAsync(shaderPlan)).Succeeded);

        foreach (var manifest in manifests)
        {
            Assert.True(File.Exists(manifest), $"{manifest} was removed with a shader cache");
        }
    }

    /// <summary>
    /// The reader is shared, so the libraries a row names must be the ones that decided something
    /// about that row's own games. The shader row's game is found nowhere while a library is
    /// unplugged, and the artwork row's game is installed, so only the shader row names the library.
    /// </summary>
    [Fact]
    public async Task ARowNamesOnlyTheLibrariesThatDecidedItsOwnGames()
    {
        RegisterInstall();
        WriteLibraryList(UnpluggedLibrary);
        Manifest(InstallRoot, "440", "Team Fortress 2");
        ShaderCacheFor(InstallRoot, "730");
        ArtworkFor("440");

        var steam = new SteamDiscovery(_environment);
        var shaderPlan = await ShaderRow(steam).PlanAsync();
        var artworkPlan = await ArtworkRow(steam).PlanAsync();

        Assert.Null(GameIn(shaderPlan, "730").IsInstalled);
        Assert.Contains(shaderPlan.Notes, n => n.Message.Contains(UnpluggedLibrary, StringComparison.OrdinalIgnoreCase));

        Assert.True(GameIn(artworkPlan, "440").IsInstalled);
        Assert.DoesNotContain(artworkPlan.Notes, n => n.Message.Contains(UnpluggedLibrary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A library whose <c>steamapps</c> Windows would not list may hold any game, so a game found in
    /// no other library is of unknown standing rather than uninstalled, and each row names the folder
    /// it could not list. A game found in another library is still installed.
    /// </summary>
    [Fact]
    public async Task ALibraryWhoseRecordsWillNotBeListedLeavesAGameUnknownAndIsNamed()
    {
        RegisterInstall();
        WriteLibraryList(SecondLibrary);
        Manifest(InstallRoot, "440", "Team Fortress 2");
        Manifest(SecondLibrary, "570", "Dota 2");

        foreach (var appId in new[] { "440", "570" })
        {
            ShaderCacheFor(InstallRoot, appId);
            ArtworkFor(appId);
        }

        var records = Path.Combine(SecondLibrary, "steamapps");
        using var denied = new DeniedDirectory(records);

        var steam = new SteamDiscovery(_environment);

        foreach (var plan in new[] { await ShaderRow(steam).PlanAsync(), await ArtworkRow(steam).PlanAsync() })
        {
            Assert.True(GameIn(plan, "440").IsInstalled);
            Assert.Null(GameIn(plan, "570").IsInstalled);

            var note = Assert.Single(plan.Notes, n => n.Message.Contains(records, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(PlanNoteSeverity.Information, note.Severity);
            Assert.DoesNotContain(@"\\?\", note.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// "Not installed" needs every library listed. Where each one was, a game in none of them is
    /// uninstalled, and no library is named.
    /// </summary>
    [Fact]
    public void AGameInNoLibraryIsUninstalledOnlyWhereEveryLibraryWasListed()
    {
        WriteFile(Path.Combine(SecondLibrary, "steamapps", "libraryfolder.vdf"));
        var listed = Path.Combine(_temp.Path, "listed", "SteamLibrary");
        Manifest(listed, "440", "Team Fortress 2");

        var reader = new SteamAppManifests(Libraries(
            new SteamLibrary(listed, PathPresence.Present),
            new SteamLibrary(SecondLibrary, PathPresence.Present)));

        var gone = reader.Describe("730");

        Assert.Equal(new SteamApp("730", null, false), gone);
        Assert.Empty(reader.UnreachedLibraryNotes([gone], refusalsNamed: false));
        Assert.Equal(new SteamApp("440", "Team Fortress 2", true), reader.Describe("440"));
    }

    /// <summary>
    /// RetroArch's discovery reads through the same reader. A library whose records will not be
    /// listed may hold RetroArch, so its manifest there is reported unread rather than absent, and a
    /// library that lists it yields the folder Steam installed it in. A row that already named the
    /// game read the manifest, and it is not read again.
    /// </summary>
    [Fact]
    public void InstallFoldersReportAManifestInALibraryThatWillNotBeListedAsUnread()
    {
        var listed = Path.Combine(_temp.Path, "listed", "SteamLibrary");
        Manifest(listed, RetroArchDiscovery.SteamAppId, "RetroArch");
        Directory.CreateDirectory(Path.Combine(SecondLibrary, "steamapps"));

        var reader = new SteamAppManifests(Libraries(
            new SteamLibrary(listed, PathPresence.Present),
            new SteamLibrary(SecondLibrary, PathPresence.Present),
            new SteamLibrary(UnpluggedLibrary, PathPresence.Absent)));

        using var denied = new DeniedDirectory(Path.Combine(SecondLibrary, "steamapps"));

        Assert.Equal("RetroArch", reader.Describe(RetroArchDiscovery.SteamAppId).Name);

        var (folders, unread) = reader.InstallFoldersOf(RetroArchDiscovery.SteamAppId);

        Assert.Equal((listed, Path.Combine(listed, "steamapps", "common", "RetroArch")), Assert.Single(folders));
        Assert.Equal(
            Path.Combine(SecondLibrary, SteamAppManifests.RelativePath(RetroArchDiscovery.SteamAppId)),
            Assert.Single(unread));
        Assert.Equal(1, reader.Reads);
    }

    /// <summary>
    /// The listings are for one pass: a game installed while the app is open is seen once the pass
    /// ends, and not before, so two rows in one pass cannot disagree about it.
    /// </summary>
    [Fact]
    public async Task TheRecordsAreListedOncePerPassAndAgainAfterInvalidation()
    {
        RegisterInstall();
        WriteLibraryList();
        ShaderCacheFor(InstallRoot, "730");

        var steam = new SteamDiscovery(_environment);
        var row = ShaderRow(steam);

        Assert.False(GameIn(await row.PlanAsync(), "730").IsInstalled);

        Manifest(InstallRoot, "730", "Counter-Strike 2");

        Assert.False(GameIn(await row.PlanAsync(), "730").IsInstalled);
        Assert.Equal(1, steam.Manifests!.Listings);

        row.InvalidateCaches();

        Assert.Equal(new SteamApp("730", "Counter-Strike 2", true), GameIn(await row.PlanAsync(), "730"));
    }

    /// <summary>
    /// The rows plan concurrently, so many threads ask about the same games at once. Released together
    /// at a barrier, they still list each library once and read each manifest once, and agree on
    /// every answer.
    /// </summary>
    [Fact]
    public async Task ThreadsAskingAtOnceListAndReadOnce()
    {
        var first = Path.Combine(_temp.Path, "first", "SteamLibrary");
        var second = Path.Combine(_temp.Path, "second", "SteamLibrary");
        string[] ids = ["440", "570", "730", "1000"];
        Manifest(first, "440", "Team Fortress 2");
        Manifest(second, "570", "Dota 2");
        Manifest(second, "730", "Counter-Strike 2");

        const int threads = 16;

        for (var round = 0; round < 20; round++)
        {
            var reader = new SteamAppManifests(Libraries(
                new SteamLibrary(first, PathPresence.Present),
                new SteamLibrary(second, PathPresence.Present)));

            using var barrier = new Barrier(threads);

            var answers = await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    return ids.Select((_, i) => reader.Describe(ids[(i + thread) % ids.Length])).OrderBy(a => a.Id).ToList();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)));

            Assert.Equal(2, reader.Listings);
            Assert.Equal(3, reader.Reads);
            Assert.All(answers, answer => Assert.Equal(answers[0], answer));
        }
    }

    private SteamLibraries Libraries(params SteamLibrary[] folders) =>
        new(folders, SteamLibraryListing.Read, Path.Combine(_temp.Path, "libraryfolders.vdf"));
}
