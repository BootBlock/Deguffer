using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The first provider whose cache sits beside something that cannot be fetched again for free.
///
/// <para>Two things have to hold. Nothing but <c>Data</c> is ever reachable, and the downloads, their
/// index and the settings are asserted to survive a run rather than merely left out. And where
/// Spotify's settings have moved its storage, or cannot be read, no cache the downloads may be in is
/// offered. That is asserted on the plan and on the declaration, which are what a deletion is built
/// from. A plan with no step removes nothing, so running one would add no evidence.</para>
/// </summary>
public sealed class SpotifyCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SpotifyCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string LocalFolder => Path.Combine(_environment.LocalAppData, "Spotify");

    private string RoamingFolder => Path.Combine(_environment.RoamingAppData, "Spotify");

    private string Package =>
        Path.Combine(_environment.LocalAppData, "Packages", SpotifyEdition.StorePackageFamily);

    private string StoreCacheFolder => Path.Combine(Package, "LocalCache", "Spotify");

    private string StoreSettingsFolder => Path.Combine(Package, "LocalState", "Spotify");

    private SpotifyCacheProvider CreateProvider(FakeDirectoryScanner? scanner = null) =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner);

    /// <summary>A directory with a file in it, so it measures above zero and is selectable.</summary>
    private static string Populate(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "data.bin"), new byte[4096]);
        return path;
    }

    /// <summary>A settings file in Spotify's own form: LF line endings, one key per line.</summary>
    private static string WriteSettings(string folder, params string[] lines)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "prefs");
        File.WriteAllText(file, string.Join('\n', lines) + "\n");
        return file;
    }

    /// <summary>A path written as Spotify writes a string value, with every backslash doubled.</summary>
    private static string Quoted(string path) => "\"" + path.Replace(@"\", @"\\") + "\"";

    private static void AssertNothingIsOffered(SpotifyCacheProvider provider, CleanupPlan plan)
    {
        Assert.Empty(plan.TargetedPaths);
        Assert.All(provider.Roots, root => Assert.Empty(root.Locations));
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// An edition whose settings folder Windows will not describe may be installed, so §5.6 asserts
    /// what it keeps there, recorded as a refusal. Read as absent, the edition counted as not
    /// installed and its settings, downloads index and accounts were never asserted at all.
    /// </summary>
    [Fact]
    public async Task AnEditionWhoseSettingsFolderWindowsWillNotDescribeStillHasItsSurvivorsAsserted()
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        Directory.CreateDirectory(StoreSettingsFolder);

        using var denied = DeniedDirectory.WithUnreadableAttributes(StoreSettingsFolder);

        var plan = await CreateProvider().PlanAsync();

        Assert.False(Directory.Exists(StoreCacheFolder));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(StoreSettingsFolder, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Refused);
    }

    [Fact]
    public async Task ReportsNotPresentOnAMachineWithNoSpotify()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheStreamingCacheAndNothingElse()
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));
        Populate(Path.Combine(LocalFolder, "Storage"));
        Populate(Path.Combine(LocalFolder, "Browser"));
        WriteSettings(RoamingFolder, "app.autostart-mode=\"off\"", "storage.size=1024");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(cache, Assert.Single(plan.TargetedPaths));
        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.False(plan.WasNotExamined);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlansTheStoreEditionsCache()
    {
        var cache = Populate(Path.Combine(StoreCacheFolder, "Data"));
        Populate(Path.Combine(StoreSettingsFolder, "Storage"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        Assert.Equal(cache, Assert.Single((await provider.PlanAsync()).TargetedPaths));
    }

    /// <summary>
    /// §5.6's negative, in both editions. The downloads, the record of them and the settings file are
    /// each asserted by name, and the run is executed so the survival is observed rather than
    /// inferred from the plan.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDownloadsTheirRecordAndTheSettingsAllSurvive(bool store)
    {
        var cacheFolder = store ? StoreCacheFolder : LocalFolder;
        var settingsFolder = store ? StoreSettingsFolder : RoamingFolder;
        var downloadsFolder = store ? StoreSettingsFolder : LocalFolder;

        var cache = Populate(Path.Combine(cacheFolder, "Data"));
        Populate(Path.Combine(cache, "0a"));

        string[] directories =
        [
            cacheFolder,
            Populate(Path.Combine(downloadsFolder, "Storage")),
            settingsFolder,
            Populate(Path.Combine(settingsFolder, "Users")),
        ];

        string[] files =
        [
            WriteSettings(settingsFolder, "app.autostart-mode=\"off\""),
            Path.Combine(downloadsFolder, "offline.bnk"),
        ];

        File.WriteAllBytes(files[1], new byte[128]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(cache, Assert.Single(plan.TargetedPaths));

        foreach (var path in directories.Concat(files))
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache), $"{cache} was not removed");
        Assert.All(directories, d => Assert.True(Directory.Exists(d), $"{d} was removed"));
        Assert.All(files, f => Assert.True(File.Exists(f), $"{f} was removed"));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe. The folder is never
    /// enumerated, so a neighbour the table does not name is unreachable by construction, and this is
    /// the assertion that the construction holds.
    /// </summary>
    [Theory]
    [InlineData("Browser")]
    [InlineData("Update")]
    [InlineData("Crash Reports")]
    [InlineData("something-unrecognised")]
    public async Task AnUnrecognisedNeighbourIsNeverATarget(string name)
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        var neighbour = Populate(Path.Combine(LocalFolder, name));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(neighbour, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.True(Directory.Exists(neighbour), $"{neighbour} was removed");
    }

    /// <summary>
    /// Spotify's settings have moved its storage somewhere of the user's choosing. The cache in the
    /// usual place is still offered. The moved location is named and asserted to survive, and nothing
    /// in it is measured: its size would be a figure about downloads Deguffer is not going to touch.
    /// </summary>
    [Fact]
    public async Task AMovedStorageIsNeverMeasuredAndSurvives()
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));
        var moved = Populate(Path.Combine(_temp.Path, "music", "Spotify"));
        Populate(Path.Combine(moved, "0a"));
        WriteSettings(RoamingFolder, $"storage.location={Quoted(moved)}");

        var scanner = new FakeDirectoryScanner();
        var provider = CreateProvider(scanner);
        var plan = await provider.PlanAsync();

        Assert.Equal(cache, Assert.Single(plan.TargetedPaths));

        // The premise: the scanner records measurements, so its silence about the moved location
        // below is evidence rather than a scanner that saw nothing at all.
        Assert.Contains(cache, scanner.Measured, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(scanner.Measured, path => LongPath.Contains(moved, path));

        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("did not measure or remove", StringComparison.Ordinal)
            && n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A moved storage that is the cache, sits inside it or holds it. Downloaded music may be in the
    /// cache folder, so the cache is neither offered nor declared, and the note says why.
    /// </summary>
    [Theory]
    [InlineData(SpotifySettings.LocationKey, "Data")]
    [InlineData(SpotifySettings.LocationKey, @"Data\offline")]
    [InlineData(SpotifySettings.LocationKey, "")]
    [InlineData(SpotifySettings.PreviousLocationKey, "Data")]
    public async Task AStorageLocationOverlappingTheCacheWithholdsIt(string key, string relative)
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));
        var location = relative.Length == 0 ? LocalFolder : Path.Combine(LocalFolder, relative);
        WriteSettings(RoamingFolder, $"{key}={Quoted(location)}");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        // The installer edition's root only. The Store edition's cache overlaps nothing here, so it is
        // still declared, and nothing of it is on disk to target.
        Assert.Empty(plan.TargetedPaths);
        Assert.Empty(provider.Roots[0].Locations);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(cache, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("left the cache alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));

        // A location that is the cache folder names that folder once. Saying it "overlaps" itself
        // would name the same path twice as though it were two folders.
        var isTheCache = location.Equals(cache, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(
            isTheCache ? "name its cache folder" : "overlaps its cache", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(
            isTheCache ? "overlaps its cache" : "name its cache folder", StringComparison.Ordinal));

        // One sentence for the location. The one about the cache already names it, and a second
        // saying it was moved would read as two different folders.
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("did not measure or remove", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cache the settings withhold is withheld and explained even where Windows will not describe
    /// it. The two-state probe dropped it from the withheld list, so the plan said nothing about the
    /// overlap and called the location an ordinary move. It is named as unreached as well, and it is
    /// asserted as a survivor recorded as a refusal, so §5.6 checks it again after the run.
    /// </summary>
    [Fact]
    public async Task AWithheldCacheWindowsWillNotDescribeIsStillWithheldAndExplained()
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));
        WriteSettings(RoamingFolder, $"{SpotifySettings.LocationKey}={Quoted(Path.Combine(cache, "offline"))}");

        using var denied = DeniedDirectory.WithUnreadableAttributes(cache);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(cache, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("left the cache alone", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(cache, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("did not measure or remove", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Refused);
    }

    /// <summary>
    /// A moved storage that holds where a cache would be, with no cache there. Nothing is withheld,
    /// so no sentence about a cache applies, and the location still has to be named: the row must
    /// read neither "Not installed" nor "Already clear" about a folder nobody examined.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMovedStorageHoldingAMissingCacheIsStillPresentAndNamed(bool aboveSpotify)
    {
        var location = aboveSpotify ? _environment.LocalAppData : LocalFolder;
        Directory.CreateDirectory(LocalFolder);
        WriteSettings(RoamingFolder, $"storage.location={Quoted(location)}");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("did not measure or remove", StringComparison.Ordinal)
            && n.Message.Contains(location, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(location, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task ALockedSettingsFileWithholdsTheCache()
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        var settings = WriteSettings(RoamingFolder, "app.autostart-mode=\"off\"");

        var provider = CreateProvider();
        CleanupPlan plan;

        using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await provider.IsPresentAsync());
            plan = await provider.PlanAsync();
        }

        AssertNothingIsOffered(provider, plan);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("could not read Spotify's settings", StringComparison.Ordinal)
            && n.Message.Contains("left Spotify's streaming cache alone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASettingsFileLargerThanSpotifyWritesWithholdsTheCache()
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        Directory.CreateDirectory(RoamingFolder);
        File.WriteAllBytes(Path.Combine(RoamingFolder, "prefs"), new byte[(1024 * 1024) + 1]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        AssertNothingIsOffered(provider, plan);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read Spotify's settings", StringComparison.Ordinal));
    }

    /// <summary>
    /// A location Deguffer cannot place is not skipped. Skipping it would read as "nothing moved",
    /// and the cache the downloads may have moved into would be offered. The last case is a file that
    /// is not UTF-8 text at all.
    /// </summary>
    [Theory]
    [InlineData("storage.location=\"relative\\\\folder\"")]
    [InlineData("storage.location=C:\\\\unquoted")]
    [InlineData("storage.location=\"C:\\\\music\\q\"")]
    [InlineData("storage.location=\"C:\\\\mu\"sic\"")]
    [InlineData("storage.location=\"C:\\\\Mus\u00e9e\"")]
    public async Task ASettingDegufferCannotPlaceWithholdsTheCache(string line)
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        Directory.CreateDirectory(RoamingFolder);

        // Written in Windows-1252, where 'é' is one byte that is not UTF-8. Every other line is plain
        // ASCII and reads the same in either encoding.
        File.WriteAllBytes(
            Path.Combine(RoamingFolder, "prefs"),
            System.Text.Encoding.Latin1.GetBytes(line + "\n"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        AssertNothingIsOffered(provider, plan);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not make sense of", StringComparison.Ordinal));
    }

    /// <summary>
    /// One edition's settings that cannot be read withhold the other edition's cache too, because a
    /// location is a path and the unread one could name either.
    /// </summary>
    [Fact]
    public async Task OneEditionsUnplaceableSettingWithholdsTheOtherEditionsCache()
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        WriteSettings(StoreSettingsFolder, "storage.location=\"relative\"");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        AssertNothingIsOffered(provider, plan);
    }

    /// <summary>
    /// A location that can be placed, beside one that cannot. The caches are withheld for the second,
    /// and the first is still protected: it names where downloads may be whatever the other line says.
    /// </summary>
    [Fact]
    public async Task ALocationBesideOneThatCannotBePlacedIsStillProtected()
    {
        Populate(Path.Combine(LocalFolder, "Data"));
        var moved = Populate(Path.Combine(_temp.Path, "music", "Spotify"));
        WriteSettings(
            RoamingFolder,
            "storage.location=\"relative\"",
            $"storage.last-location={Quoted(moved)}");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        AssertNothingIsOffered(provider, plan);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>The downloads' usual place, written out, is not a move and owes no sentence.</summary>
    [Fact]
    public async Task TheUsualDownloadsFolderWrittenOutIsNotAMove()
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));
        WriteSettings(RoamingFolder, $"storage.location={Quoted(Path.Combine(LocalFolder, "Storage"))}");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(cache, Assert.Single(plan.TargetedPaths));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("storage", StringComparison.Ordinal));
    }

    /// <summary>
    /// A location is a path, so one edition can be pointed inside the other's cache. The cache it
    /// overlaps is withheld and survives a run that removes the other edition's cache.
    /// </summary>
    [Fact]
    public async Task OneEditionsSettingsCanWithholdTheOtherEditionsCache()
    {
        var classic = Populate(Path.Combine(LocalFolder, "Data"));
        var store = Populate(Path.Combine(StoreCacheFolder, "Data"));
        WriteSettings(RoamingFolder, $"storage.location={Quoted(store)}");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(classic, Assert.Single(plan.TargetedPaths));

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.False(Directory.Exists(classic), $"{classic} was not removed");
        Assert.True(File.Exists(Path.Combine(store, "data.bin")), "the withheld cache was emptied");
    }

    /// <summary>
    /// Spotify is installed with its storage moved and no cache in the usual place. The row must not
    /// read "Not installed", and it must not read "Already clear" either: the moved location was never
    /// examined.
    /// </summary>
    [Fact]
    public async Task AMovedStorageWithNoCacheIsPresentAndUnexamined()
    {
        var moved = Populate(Path.Combine(_temp.Path, "music", "Spotify"));
        WriteSettings(RoamingFolder, $"storage.location={Quoted(moved)}");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A cache moved onto another drive with a link. Deguffer offers nothing through it and says so,
    /// rather than deleting the far side of a redirection nobody classified.
    /// </summary>
    [Fact]
    public async Task AJunctionedCacheIsLeftAloneAndReported()
    {
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        Directory.CreateDirectory(LocalFolder);
        SymbolicLink.ToDirectory(Path.Combine(LocalFolder, "Data"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
    }

    /// <summary>
    /// G4: the settings are read once per planning pass, and again after an invalidation. A storage
    /// moved while the app was open is honoured on the next preview, not on the next launch.
    /// </summary>
    [Fact]
    public async Task TheSettingsAreReadOncePerPassAndAgainAfterInvalidation()
    {
        var cache = Populate(Path.Combine(LocalFolder, "Data"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        WriteSettings(RoamingFolder, $"storage.location={Quoted(cache)}");

        Assert.Equal(cache, Assert.Single((await provider.PlanAsync()).TargetedPaths));

        provider.InvalidateCaches();

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// The whole table, read back: two editions and exactly one path under each. Adding a second
    /// location, <c>Storage</c> being the one that would matter, fails here rather than in a deletion.
    /// </summary>
    [Fact]
    public void TheDeclarationNamesTheTwoCachesAndNothingElse()
    {
        var provider = CreateProvider();

        Assert.Equal(new[] { LocalFolder, Package }, provider.Roots.Select(r => r.Path));
        Assert.Equal(
            new[]
            {
                Path.Combine(LocalFolder, "Data"),
                Path.Combine(StoreCacheFolder, "Data"),
            },
            provider.Roots.SelectMany(
                root => root.Locations.Select(l => Path.Combine(root.Path, l.RelativePath))));
    }
}
