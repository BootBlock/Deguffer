using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The library artwork Steam keeps per game in <c>appcache\librarycache</c> under its install
/// directory, beside Steam's own index of that artwork and a short walk from the games.
///
/// <para>Three things have to hold. Only a child named as a Steam application id, or a picture an older
/// client named after one, is ever a target. Steam's index, the folder itself and everything Steam
/// keeps beside it survive a run, asserted by name. And a game kept is kept whichever layout its
/// artwork is in.</para>
/// </summary>
public sealed class SteamLibraryArtworkProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SteamLibraryArtworkProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string InstallRoot => Path.Combine(_temp.Path, "games", "Steam");

    private string Container => Path.Combine(InstallRoot, "appcache", "librarycache");

    private SteamLibraryArtworkProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    /// <summary>The install, recorded as Steam records it, with the client beside it.</summary>
    private string RegisterInstall()
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllBytes(Path.Combine(InstallRoot, "steam.exe"), new byte[64]);

        _environment.WithRegistryValue(
            SteamDiscovery.RegistryKey, SteamDiscovery.InstallPathValue, InstallRoot.Replace('\\', '/'));

        return InstallRoot;
    }

    /// <summary>One game's artwork, laid out as the current client lays it out.</summary>
    private string ArtworkFor(string appId)
    {
        var folder = Path.Combine(Container, appId);
        WriteFile(Path.Combine(folder, "header.jpg"));
        WriteFile(Path.Combine(folder, "library_600x900.jpg"));
        WriteFile(Path.Combine(folder, "3f2a9c", "logo.png"));
        return folder;
    }

    private string Index() => WriteFile(Path.Combine(Container, "assetcache.vdf"));

    private string Manifest(string appId, string name)
    {
        var path = Path.Combine(InstallRoot, "steamapps", $"appmanifest_{appId}.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            "AppState"
            {
                "appid"		"{{appId}}"
                "name"		"{{name}}"
            }
            """);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSteam()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(provider.ToolRoots);
    }

    /// <summary>
    /// Steam's index alone, or a game's folder holding only empty folders, is nothing to reclaim, so it
    /// is no row.
    /// </summary>
    [Fact]
    public async Task TheIndexAndEmptyGameFoldersAreNoRow()
    {
        RegisterInstall();
        Index();
        Directory.CreateDirectory(Path.Combine(Container, "440", "3f2a9c"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>An unrecognised child is never offered, so a row for it alone would offer nothing.</summary>
    [Fact]
    public async Task AnUnrecognisedChildAloneIsNoRow()
    {
        RegisterInstall();
        Populate(Path.Combine(Container, "backup"));
        WriteFile(Path.Combine(Container, "notes.txt"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The row a reader chooses from: one item per game, Tier 2 because a picture replaced by hand is
    /// lost, each named as Steam names it where a manifest says, keyed by its id so it can be kept,
    /// and in display form rather than the extended-length form the folder was listed in.
    /// </summary>
    [Fact]
    public async Task EachGamesArtworkIsAnItemNamedFromItsManifest()
    {
        RegisterInstall();
        Index();
        var installed = ArtworkFor("440");
        var owned = ArtworkFor("570");
        Manifest("440", "Team Fortress 2");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.Equal(
            new[] { installed, owned }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.TargetedPaths, path => Assert.False(path.StartsWith(@"\\?\", StringComparison.Ordinal), path));

        var steps = plan.Steps.OfType<DeleteDirectoryStep>().ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(new ItemIdentity("440", "Team Fortress 2"), steps[installed].Identity);
        Assert.Contains(new ItemFacet("Installed", "Yes"), steps[installed].Facets);
        Assert.Equal(new ItemIdentity("570", "Steam app 570"), steps[owned].Identity);
        Assert.Contains(new ItemFacet("Installed", "No"), steps[owned].Facets);
    }

    /// <summary>
    /// An older client wrote each picture straight into the folder as <c>&lt;appid&gt;_&lt;art&gt;</c>.
    /// Those are file targets keyed by their game. A file named like one that is not a picture, or a
    /// picture not named after a game, is something nobody established, and stays.
    /// </summary>
    [Fact]
    public async Task AnOlderClientsPicturesAreFileTargetsKeyedByTheirGame()
    {
        RegisterInstall();
        var header = WriteFile(Path.Combine(Container, "440_header.jpg"));
        var logo = WriteFile(Path.Combine(Container, "440_logo.PNG"));
        var notPicture = WriteFile(Path.Combine(Container, "440_notes.txt"));
        var notGame = WriteFile(Path.Combine(Container, "wallpaper_header.jpg"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        var files = plan.Steps.OfType<DeleteFileStep>().ToList();

        Assert.Equal(
            new[] { header, logo }.Order(StringComparer.OrdinalIgnoreCase),
            files.Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(files, s => Assert.Equal("440", s.Identity!.Key));

        foreach (var stranger in new[] { notPicture, notGame })
        {
            Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(header));
        Assert.False(File.Exists(logo));
        Assert.True(File.Exists(notPicture) && File.Exists(notGame));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A name is only half of what makes an entry recognised: a game's artwork is a folder named as
    /// a game, and an older client's picture is a file. A file named as a game and a folder named as a
    /// picture are neither, so each is named, protected and still there after the run.
    /// </summary>
    [Fact]
    public async Task AnEntryOfTheWrongKindForItsNameIsLeftAlone()
    {
        RegisterInstall();
        var game = ArtworkFor("440");
        var fileNamedAsGame = WriteFile(Path.Combine(Container, "570"));
        var folderNamedAsPicture = Populate(Path.Combine(Container, "570_header.jpg"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([game], plan.TargetedPaths);

        foreach (var stranger in new[] { fileNamedAsGame, folderNamedAsPicture })
        {
            Assert.Contains(plan.Notes, n => n.Message.Contains(stranger, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(fileNamedAsGame), $"{fileNamedAsGame} was removed");
        Assert.True(Directory.Exists(folderNamedAsPicture), $"{folderNamedAsPicture} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The remedy the row offers for a hand-edited picture: keeping a game keeps all of its artwork,
    /// its folder and an older client's loose files alike, and nothing of another game's.
    /// </summary>
    [Fact]
    public async Task KeepingAGameKeepsItsArtworkInEitherLayout()
    {
        RegisterInstall();
        var folder = ArtworkFor("440");
        var loose = WriteFile(Path.Combine(Container, "440_library_hero.jpg"));
        var other = ArtworkFor("570");

        var plan = (await CreateProvider().PlanAsync())
            .WithKeepList(new HashSet<string>(["440"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal([other], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(loose, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2's dangerous direction inside the container: a folder whose name is not an application id
    /// is not a target, is named on the plan, and is still there after the run.
    /// </summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("440.old")]
    [InlineData("-440")]
    [InlineData("99999999999")]
    public async Task AChildThatIsNotAnApplicationIdIsLeftAloneAndNamed(string name)
    {
        RegisterInstall();
        ArtworkFor("440");
        var stranger = Populate(Path.Combine(Container, name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(stranger, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(stranger), $"{stranger} was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.6's negative: Steam's index, the folder, the web cache beside it, Steam's other indexes, the
    /// games, the download in progress, the accounts and Steam's configuration all survive a run that
    /// removed every game's artwork. Each is asserted by name, not by an assertion on a folder above it.
    /// </summary>
    [Fact]
    public async Task SteamsIndexTheGamesAndEverythingBesideTheArtworkSurvive()
    {
        var install = RegisterInstall();
        var artwork = new[] { ArtworkFor("440"), ArtworkFor("570") };

        var files = new List<string>
        {
            Index(),
            WriteFile(Path.Combine(install, "appcache", "appinfo.vdf")),
            WriteFile(Path.Combine(install, "appcache", "packageinfo.vdf")),
            Manifest("440", "Team Fortress 2"),
        };

        var directories = new[]
        {
            Populate(Path.Combine(install, "steamapps", "common", "Team Fortress 2")),
            Populate(Path.Combine(install, "steamapps", "downloading", "440")),
            Populate(Path.Combine(install, "userdata", "12345", "config", "grid")),
            Populate(Path.Combine(install, "config")),
            // Not this row's to protect, since the web cache row may take it in the same run, but this
            // row must not take it either.
            Populate(Path.Combine(install, "appcache", "httpcache")),
            Container,
        };

        string[] named =
        [
            install,
            Container,
            Path.Combine(install, "steamapps"),
            Path.Combine(install, "steamapps", "common"),
            Path.Combine(install, "steamapps", "downloading"),
            Path.Combine(install, "userdata"),
            Path.Combine(install, "config"),
            .. files.Take(3),
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(artwork.Order(StringComparer.OrdinalIgnoreCase), plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var path in named)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.All(artwork, a => Assert.False(Directory.Exists(a), $"{a} was not removed"));
        Assert.All(directories, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.All(files, f => Assert.True(File.Exists(f), $"{f} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A game's folder that is a link is named and never followed.</summary>
    [Fact]
    public async Task ALinkedGameFolderIsNeverFollowed()
    {
        RegisterInstall();
        ArtworkFor("440");
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere", "570"));
        var link = Path.Combine(Container, "570");
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(link, StringComparison.OrdinalIgnoreCase));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(outside), $"{outside} was removed through the link");
    }

    /// <summary>
    /// Artwork moved to another drive with a link is never looked through, and the row is never
    /// called clear, because nothing there was looked at.
    /// </summary>
    [Fact]
    public async Task ALinkedArtworkFolderIsNeverLookedThrough()
    {
        var install = RegisterInstall();
        var outside = Path.Combine(_temp.Path, "elsewhere", "librarycache");
        var far = Populate(Path.Combine(outside, "440"));
        Directory.CreateDirectory(Path.Combine(install, "appcache"));
        Directory.CreateSymbolicLink(Container, outside);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(far), $"{far} was removed through the link");
    }

    /// <summary>A folder Windows will not list may hold anything, so it is presence and a refusal.</summary>
    [Fact]
    public async Task AnArtworkFolderWindowsWillNotListIsPresenceAndUnreadable()
    {
        RegisterInstall();
        ArtworkFor("440");

        using var denied = new DeniedDirectory(Container);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
    }

    /// <summary>A Steam whose install cannot be found is present and never called clear.</summary>
    [Fact]
    public async Task ASteamWhoseInstallCannotBeFoundIsPresentAndUnexamined()
    {
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Steam"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>§5.3: the client writes this folder while it shows the library.</summary>
    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    public async Task APlanMadeWhileSteamRunsSaysSo(string process)
    {
        RegisterInstall();
        ArtworkFor("440");

        var provider = new SteamLibraryArtworkProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §7.1: Explore reads the same rule. Inside the install directory, with both Steam rows'
    /// declarations, only a game's artwork and the web cache can go. The folder, Steam's index and
    /// anything unrecognised are refused, and so is a child whose name passes and whose kind does not:
    /// a file named as a game, a folder named as a picture, and a game's folder that is a link.
    /// </summary>
    [Theory]
    [InlineData(@"appcache\librarycache", false)]                   // the container
    [InlineData(@"appcache\librarycache\assetcache.vdf", false)]    // Steam's index of it
    [InlineData(@"appcache\librarycache\440", true)]                // one game's artwork
    [InlineData(@"appcache\librarycache\440\header.jpg", true)]     // and everything in it
    [InlineData(@"appcache\librarycache\440_header.jpg", true)]     // an older client's picture
    [InlineData(@"appcache\librarycache\440_notes.txt", false)]
    [InlineData(@"appcache\librarycache\440.old", false)]
    [InlineData(@"appcache\librarycache\backup", false)]
    [InlineData(@"appcache\librarycache\570", false)]               // a file named as a game
    [InlineData(@"appcache\librarycache\570_header.jpg", false)]    // a folder named as a picture
    [InlineData(@"appcache\librarycache\730", false)]               // a game's folder that is a link
    [InlineData(@"appcache\librarycache\730\header.jpg", false)]
    [InlineData(@"appcache\httpcache", true)]                       // the web cache row's
    [InlineData("steamapps", false)]
    public void ExploreOffersOnlyAGamesArtworkInsideTheFolder(string relative, bool allowed)
    {
        var install = RegisterInstall();
        Index();
        ArtworkFor("440");
        WriteFile(Path.Combine(Container, "440_header.jpg"));
        WriteFile(Path.Combine(Container, "440_notes.txt"));
        Populate(Path.Combine(Container, "440.old"));
        Populate(Path.Combine(Container, "backup"));
        WriteFile(Path.Combine(Container, "570"));
        Populate(Path.Combine(Container, "570_header.jpg"));
        Directory.CreateSymbolicLink(
            Path.Combine(Container, "730"), Populate(Path.Combine(_temp.Path, "elsewhere", "730")));

        var policy = new ExploreActionPolicy(
            [],
            [
                .. new SteamCacheProvider(_environment).ToolRoots,
                .. CreateProvider().ToolRoots,
            ],
            new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(Path.Combine(install, relative)).IsAllowed);
    }
}
