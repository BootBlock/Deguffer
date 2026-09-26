using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// After Effects' disk cache, found only in the folder its own preferences name.
///
/// <para>Four things have to hold. Only the folder After Effects names for this computer is ever a
/// target, and what is beside it and above it survives a run, asserted by name and refused in Explore,
/// because the chosen folder is often one the user keeps other things in. A version whose
/// preferences name no folder is not guessed at. Nothing is offered or removed while After Effects
/// runs. And in the temporary folder, where After Effects caches by default, the cache is offered once,
/// on this row, and the temporary-files row's run and this one's both verify.</para>
/// </summary>
public sealed class AfterEffectsDiskCacheProviderTests : IDisposable
{
    private const string Version = "24.6";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public AfterEffectsDiskCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>A folder on another drive the user chose for the cache, and keeps other things in.</summary>
    private string Chosen => Path.Combine(_temp.Path, "scratch-drive", "Media");

    private static string CacheName => AfterEffectsDiskCacheLayout.CacheName(FakeUserEnvironment.Machine);

    private AfterEffectsDiskCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private static string WriteFile(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// One version's preferences, in the shape After Effects writes them, naming
    /// <paramref name="folder"/> for the disk cache, or no folder at all where it is null.
    /// </summary>
    private string Preferences(string version, string? folder, string? fileName = null)
    {
        var path = Path.Combine(
            AfterEffectsDiskCacheLayout.PreferencesRoot(_environment.RoamingAppData),
            version,
            fileName ?? $"Adobe After Effects {version} Prefs.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n",
        [
            "# Text File Version 1.1",
            "# After Effects Preferences",
            "",
            "[\"Disk Cache Controls\"]",
            "\t\"Enabled 2\" = \"1\"",
            .. folder is null ? Array.Empty<string>() : [$"\t\"Folder 7\" = \"{folder}\""],
            "\t\"Max Size 3\" = \"23\"",
            "",
        ]));

        return path;
    }

    /// <summary>A version's cache below <paramref name="folder"/>, holding a rendered frame.</summary>
    private static string Cache(string folder, string version, string name)
    {
        var cache = Path.Combine(AfterEffectsDiskCacheLayout.VersionsUnder(folder), version, name);
        WriteFile(Path.Combine(cache, "0a", "frame-000123.bin"));
        return cache;
    }

    [Fact]
    public async Task ReportsNotPresentWhereAfterEffectsKeptNoPreferences()
    {
        Cache(Chosen, Version, CacheName);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
        Assert.Empty(await provider.DiscoverToolRootsAsync());
    }

    [Fact]
    public void IsTierTwoAndItsPartsAreOneDecision()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.Equal(StepGrain.Parts, provider.Grain);
    }

    /// <summary>
    /// §5.2 and §5.6: each version's cache named for this computer is a target, and another computer's
    /// cache, anything beside the cache, and the user's own files in the chosen folder all survive the
    /// run, on the disk as in the report.
    /// </summary>
    [Fact]
    public async Task OffersOnlyThisComputersCachesAndEverythingAroundThemSurvives()
    {
        Preferences(Version, Chosen);
        Preferences("23.0", Chosen);

        var current = Cache(Chosen, Version, CacheName);
        var older = Cache(Chosen, "23.0", CacheName);
        var otherComputer = Cache(Chosen, Version, AfterEffectsDiskCacheLayout.CacheName("RENDERNODE"));
        var beside = WriteFile(Path.Combine(AfterEffectsDiskCacheLayout.VersionsUnder(Chosen), Version, "notes.txt"));
        var footage = WriteFile(Path.Combine(Chosen, "Footage", "A001_C002.mov"));
        var adobeOther = WriteFile(Path.Combine(Chosen, "Adobe", "Premiere Pro", "project.prproj"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { current, older }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var survivor in new[]
        {
            AfterEffectsDiskCacheLayout.VersionsUnder(Chosen),
            Path.GetDirectoryName(current)!,
            Path.GetDirectoryName(older)!,
            otherComputer,
            beside,
        })
        {
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase));
        }

        // The chosen folder is the user's, and naming it would refuse everything in it in Explore.
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.Equals(Chosen, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(current));
        Assert.False(Directory.Exists(older));

        foreach (var kept in new[] { Path.Combine(otherComputer, "0a", "frame-000123.bin"), beside, footage, adobeOther })
        {
            Assert.True(File.Exists(kept), $"'{kept}' did not survive the run");
        }

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The file's name is translated, so it is the section that identifies it: a Russian install names
    /// it <c>Установки</c>, and a Spanish one puts <c>Preferencias</c> first.
    /// </summary>
    [Theory]
    [InlineData("Adobe After Effects 24.6 Установки.txt")]
    [InlineData("Preferencias Adobe After Effects 24.6.txt")]
    public async Task ReadsATranslatedPreferencesFile(string fileName)
    {
        Preferences(Version, Chosen, fileName);
        var cache = Cache(Chosen, Version, CacheName);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
    }

    /// <summary>
    /// Adobe publishes no default folder, so a version whose preferences name none is reported and
    /// not guessed at, even where a cache sits in the temporary folder After Effects often uses.
    /// </summary>
    [Fact]
    public async Task DoesNotGuessAFolderThePreferencesDoNotName()
    {
        Preferences(Version, folder: null);
        var guessed = Cache(_environment.TempPath, Version, CacheName);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("name no disk cache folder", StringComparison.Ordinal));
        Assert.True(Directory.Exists(guessed));
    }

    /// <summary>
    /// A preferences file Deguffer could not read may name a folder, so the plan says so rather than
    /// reading as clear.
    /// </summary>
    [Fact]
    public async Task SaysSoWhereAPreferencesFileCouldNotBeRead()
    {
        var preferences = Preferences(Version, Chosen);
        File.WriteAllBytes(preferences, new byte[(4 * 1024 * 1024) + 1]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(preferences, StringComparison.Ordinal));
    }

    /// <summary>§5.3: while After Effects or its renderer runs, nothing is offered, and each cache is protected and refused in Explore.</summary>
    [Theory]
    [InlineData("AfterFX")]
    [InlineData("aerender")]
    public async Task LeavesEveryCacheAloneWhileAfterEffectsRuns(string process)
    {
        Preferences(Version, Chosen);
        var cache = Cache(Chosen, Version, CacheName);

        var provider = CreateProvider(new FakeProcessInspector(process));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(cache, StringComparison.OrdinalIgnoreCase));

        var root = Assert.Single(await provider.DiscoverToolRootsAsync(), r => r.Path == Path.GetDirectoryName(cache));
        Assert.False(root.Recognises(CacheName));
    }

    /// <summary>§7.1: Explore may remove this computer's cache from a version's folder and nothing else there.</summary>
    [Fact]
    public async Task ExploreRecognisesOnlyThisComputersCache()
    {
        Preferences(Version, Chosen);
        var cache = Cache(Chosen, Version, CacheName);
        var otherComputer = Cache(Chosen, Version, AfterEffectsDiskCacheLayout.CacheName("RENDERNODE"));

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        var version = Assert.Single(roots, r => r.Path == Path.GetDirectoryName(cache));
        Assert.True(version.Recognises(CacheName));
        Assert.False(version.Recognises(Path.GetFileName(otherComputer)));
        Assert.Contains(roots, r => r.Path == AfterEffectsDiskCacheLayout.VersionsUnder(Chosen) && !r.Recognises(Version));
        Assert.DoesNotContain(roots, r => r.Path == Chosen);
    }

    /// <summary>§5.3 at the clean: After Effects started after the preview, so the cache is asked about again and kept.</summary>
    [Fact]
    public async Task AsksAgainAtTheCleanAndKeepsTheCacheIfAfterEffectsHasStarted()
    {
        Preferences(Version, Chosen);
        var cache = Cache(Chosen, Version, CacheName);
        var inspector = new FakeProcessInspector();

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);

        inspector.WithRunning("AfterFX");
        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(Path.Combine(cache, "0a", "frame-000123.bin")), "the cache was removed while After Effects ran");
    }

    /// <summary>
    /// A folder value that is there but cannot be read is reported as unread, never as a version that
    /// names no folder, which would tell the user something untrue.
    /// </summary>
    [Fact]
    public async Task AFolderValueItCannotReadIsReportedAsUnread()
    {
        var preferences = Preferences(Version, "D:\\Jos\"C3\"");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(preferences, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("name no disk cache folder", StringComparison.Ordinal));
    }

    /// <summary>
    /// A link where After Effects' own folders were expected is named and never followed, so a cache on
    /// its far side is neither offered nor removed.
    /// </summary>
    [Fact]
    public async Task ALinkBelowTheChosenFolderIsNamedRatherThanFollowed()
    {
        Preferences(Version, Chosen);
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var farCache = Cache(outside, Version, CacheName);

        Directory.CreateDirectory(Chosen);
        SymbolicLink.ToDirectory(Path.Combine(Chosen, "Adobe"), Path.Combine(outside, "Adobe"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Chosen, "Adobe"), StringComparison.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(farCache));
    }

    /// <summary>The same rule at the cache itself.</summary>
    [Fact]
    public async Task ACacheThatIsALinkIsNamedRatherThanFollowed()
    {
        Preferences(Version, Chosen);
        var outside = WriteFile(Path.Combine(_temp.Path, "elsewhere", "irreplaceable.bin"));
        var link = Path.Combine(AfterEffectsDiskCacheLayout.VersionsUnder(Chosen), Version, CacheName);

        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        SymbolicLink.ToDirectory(link, Path.GetDirectoryName(outside)!);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(outside));
    }

    /// <summary>
    /// Where the cache is in the temporary folder, it is claimed from the temporary-files row at its
    /// depth, and nothing else in that folder is named as this row's survivor. The machine's temporary
    /// folder too, which the temporary-files row also empties.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimsItsCacheFromTheTemporaryFolderAndNamesNothingElseThere(bool machineWide)
    {
        var folder = machineWide ? Path.Combine(_system.WindowsDirectory, "Temp") : _environment.TempPath;
        Directory.CreateDirectory(folder);
        Preferences(Version, folder);
        var cache = Cache(folder, Version, CacheName);
        WriteFile(Path.Combine(Path.GetDirectoryName(cache)!, "notes.txt"));

        var provider = CreateProvider();

        Assert.Equal([cache], await provider.ClaimedEntriesAsync([folder]));
        Assert.Empty(await provider.ClaimedEntriesAsync([Path.Combine(_temp.Path, "unrelated")]));

        var plan = await provider.PlanAsync();

        Assert.Equal([cache], plan.TargetedPaths);
        Assert.DoesNotContain(plan.ProtectedPaths, p => LongPath.Contains(folder, p.Path));
    }

    /// <summary>
    /// The two rows in one run, in either order, as the planner runs them: each byte is taken once, by
    /// the row that recognises it, and both runs verify against the run's shared record of what was
    /// entered.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithTheTemporaryFilesRowBothRunsVerifyInEitherOrder(bool temporaryFirst)
    {
        Preferences(Version, _environment.TempPath);
        var cache = Cache(_environment.TempPath, Version, CacheName);

        // Old enough for the temporary-files row's own cut-off, so only the claim keeps it off that row.
        TempDirectory.Age(Path.Combine(cache, "0a", "frame-000123.bin"), TimeSpan.FromDays(30));
        var abandoned = TempDirectory.Age(
            WriteFile(Path.Combine(Path.GetDirectoryName(cache)!, "abandoned.tmp"), 1024),
            TimeSpan.FromDays(30));

        var afterEffects = CreateProvider();
        var temporary = new TempDirectoryProvider(
            _environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive,
            preferences: new FakePreferences(AppPreferences.Default),
            tenants: [afterEffects]);

        var cachePlan = await afterEffects.PlanAsync();
        var temporaryPlan = await temporary.PlanAsync();

        Assert.Equal(4096, cachePlan.EstimatedBytes);
        Assert.Equal(1024, temporaryPlan.EstimatedBytes);

        var reach = RunReach.Of([cachePlan, temporaryPlan]);
        var residue = new RunResidue();
        var order = temporaryFirst
            ? new (ICleanupProvider Provider, CleanupPlan Plan)[] { (temporary, temporaryPlan), (afterEffects, cachePlan) }
            : [(afterEffects, cachePlan), (temporary, temporaryPlan)];

        foreach (var (provider, plan) in order)
        {
            var result = await provider.ExecuteAsync(plan, reach, residue);
            Assert.True(result.Verification!.Passed, $"{provider.Name}: {result.Verification.Summary}");
        }

        Assert.False(Directory.Exists(cache));
        Assert.False(File.Exists(abandoned));
        Assert.True(Directory.Exists(_environment.TempPath));
    }
}
