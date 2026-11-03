using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The <c>.dropbox.cache</c> at the top of each Dropbox folder. It sits beside the user's own synced
/// files, so what has to be shown is that only what is inside the cache goes, that the cache folder,
/// the Dropbox folder, every entry beside the cache and Dropbox's records survive and are asserted,
/// that nothing is offered while Dropbox runs, and that a folder a record names is followed only where
/// it may be a Dropbox folder.
/// </summary>
public sealed class DropboxCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeVolumeInventory _volumes = new();

    public DropboxCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
        _volumes.With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Personal => Path.Combine(_environment.UserProfile, "Dropbox");

    private string Business => Path.Combine(_environment.UserProfile, "Dropbox (Example Ltd)");

    private string PersonalCache => Path.Combine(Personal, ".dropbox.cache");

    private string RoamingRecord => Path.Combine(_environment.RoamingAppData, "Dropbox", "info.json");

    private string LocalRecord => Path.Combine(_environment.LocalAppData, "Dropbox", "info.json");

    private DropboxCacheProvider CreateProvider(FakeProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system, volumes: _volumes);

    private static string Write(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The record as Dropbox documents it, with an entry for each account given a folder.</summary>
    private static void WriteRecord(string record, string? personal = null, string? business = null)
    {
        var accounts = new Dictionary<string, object>();

        if (personal is not null)
        {
            accounts["personal"] = new { path = personal, host = 123456789, is_team = false, subscription_type = "Basic" };
        }

        if (business is not null)
        {
            accounts["business"] = new { path = business, host = 987654321, is_team = true, subscription_type = "Business" };
        }

        Write(record, JsonSerializer.Serialize(accounts));
    }

    /// <summary>
    /// One Dropbox folder as the app leaves it: staged files and folders in the cache, and beside it the
    /// user's own files and folders.
    /// </summary>
    /// <returns>What is in the cache, which goes, and what is beside it, which stays.</returns>
    private static (string[] Cached, string[] Kept) CreateDropboxFolder(string folder, int bytes = 4096) =>
    (
        [
            Write(Path.Combine(folder, ".dropbox.cache", "2026-11-01", "staged.bin"), bytes),
            Write(Path.Combine(folder, ".dropbox.cache", "upload-1f3a.tmp"), bytes),
        ],
        [
            Write(Path.Combine(folder, "Report.docx")),
            Write(Path.Combine(folder, "Photos", "Holiday", "beach.jpg")),
            Write(Path.Combine(folder, ".dropbox"), 64),
        ]);

    private static void AssertProtected(CleanupPlan plan, string path) =>
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

    [Fact]
    public async Task ReportsNotPresentWithoutARecord()
    {
        CreateDropboxFolder(Personal);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>A record that outlived the folder it names is not Dropbox on this machine.</summary>
    [Fact]
    public async Task ReportsNotPresentWhereTheRecordedFolderHasGone()
    {
        WriteRecord(RoamingRecord, personal: Personal);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty((await provider.PlanAsync()).Steps);
    }

    /// <summary>
    /// §3. Dropbox stages uploads here as well as downloads, so the row is Tier 2, and it is researched, so
    /// it is not ticked. Each account's cache is emptied in place, never removed, and a folder both records
    /// name is planned once.
    /// </summary>
    [Fact]
    public async Task EmptiesTheCacheOfEachAccountAtTier2()
    {
        CreateDropboxFolder(Personal, 1024);
        CreateDropboxFolder(Business, 1024);
        WriteRecord(RoamingRecord, personal: Personal);
        WriteRecord(LocalRecord, personal: Personal.ToUpperInvariant(), business: Business);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { PersonalCache, Path.Combine(Business, ".dropbox.cache") }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(plan.Steps, step => Assert.IsType<ClearDirectoryStep>(step));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(ProviderBasis.Researched, provider.Basis);
        Assert.Equal(4 * 1024, plan.EstimatedBytes);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// §5.6, proved by running a plan. What is in the cache goes and the cache stays. The Dropbox folder,
    /// every entry beside the cache and both records survive, and each is asserted rather than merely
    /// omitted.
    /// </summary>
    [Fact]
    public async Task EverythingButWhatIsInTheCacheSurvivesARunAndIsAsserted()
    {
        var (cached, kept) = CreateDropboxFolder(Personal);
        WriteRecord(RoamingRecord, personal: Personal);
        WriteRecord(LocalRecord, personal: Personal);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[]
        {
            Personal,
            PersonalCache,
            Path.Combine(Personal, "Report.docx"),
            Path.Combine(Personal, "Photos"),
            Path.Combine(Personal, ".dropbox"),
            RoamingRecord,
            LocalRecord,
        })
        {
            AssertProtected(plan, path);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.All(cached, path => Assert.False(File.Exists(path), $"{path} was left."));
        Assert.All(kept, path => Assert.True(File.Exists(path), $"{path} went with the cache."));
        Assert.True(Directory.Exists(PersonalCache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(PersonalCache));
        Assert.True(File.Exists(RoamingRecord));
    }

    /// <summary>The user's own files are named for what they are, so the reader can tell them from Dropbox's.</summary>
    [Fact]
    public async Task TheFilesBesideTheCacheAreNamedAsTheUsers()
    {
        CreateDropboxFolder(Personal);
        WriteRecord(RoamingRecord, personal: Personal);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(
            "Your Dropbox folder",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Personal, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "beside Dropbox's cache",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Personal, "Report.docx"), StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.3. Dropbox's own steps start with quitting it, so while it runs nothing is offered, the cache is
    /// named for why, the row does not read as clear, and Explore refuses what is in the cache.
    /// </summary>
    [Fact]
    public async Task NothingIsOfferedWhileDropboxRuns()
    {
        var (cached, _) = CreateDropboxFolder(Personal);
        WriteRecord(RoamingRecord, personal: Personal);

        var provider = CreateProvider(new FakeProcessInspector("Dropbox"));
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(
            "Dropbox is running",
            Assert.Single(plan.ProtectedPaths, p => p.Path.Equals(PersonalCache, StringComparison.OrdinalIgnoreCase)).Reason,
            StringComparison.Ordinal);

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(cached[0]).IsAllowed);

        Assert.True((await provider.ExecuteAsync(plan)).Succeeded);
        Assert.All(cached, path => Assert.True(File.Exists(path)));
    }

    /// <summary>The clean asks again, so a Dropbox started while the preview was on screen holds the cache back.</summary>
    [Fact]
    public async Task ADropboxStartedAfterThePreviewHoldsTheCacheBack()
    {
        var (cached, _) = CreateDropboxFolder(Personal);
        WriteRecord(RoamingRecord, personal: Personal);

        var inspector = FakeProcessInspector.NothingRunning;
        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.Steps);

        inspector.WithRunning("Dropbox");
        var result = await provider.ExecuteAsync(plan);

        Assert.All(cached, path => Assert.True(File.Exists(path), $"{path} was removed under a Dropbox started after the preview."));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7.1 as Explore applies it: anything in the cache may go, but the cache itself may not, and nothing
    /// beside it is this row's to allow.
    /// </summary>
    [Fact]
    public async Task ExploreRemovesWhatIsInTheCacheButNeverTheCacheItself()
    {
        var (cached, _) = CreateDropboxFolder(Personal);
        WriteRecord(RoamingRecord, personal: Personal);

        var roots = await CreateProvider().DiscoverToolRootsAsync();
        var cache = Assert.Single(roots);
        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: roots);

        Assert.Equal(PersonalCache, cache.Path, ignoreCase: true);
        Assert.True(policy.MayRemove(cached[0]).IsAllowed, policy.MayRemove(cached[0]).Reason);
        Assert.True(policy.MayRemove(Path.Combine(PersonalCache, "2026-11-01")).IsAllowed);
        Assert.False(policy.MayRemove(PersonalCache).IsAllowed);
    }

    /// <summary>
    /// A record naming somewhere no Dropbox folder may be, such as the profile, is declined and said, and
    /// nothing in it is reached. A value that is not a full path is declined the same way.
    /// </summary>
    [Fact]
    public async Task DeclinesARecordedFolderThatIsTheProfileOrRelative()
    {
        var mine = Write(Path.Combine(_environment.UserProfile, ".dropbox.cache", "staged.bin"));
        WriteRecord(RoamingRecord, personal: _environment.UserProfile, business: @"Dropbox (Example Ltd)");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("personal Dropbox folder", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains(ConfiguredFolder.NotAFullPath, StringComparison.Ordinal));

        var policy = new ExploreActionPolicy([], [], new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(Path.Combine(_environment.UserProfile, ".dropbox.cache")).IsAllowed);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// A record that is not the record Dropbox writes names no folder, and the plan says the folders it may
    /// name were not ruled out. A folder the other record names is still planned.
    /// </summary>
    [Fact]
    public async Task AnUnreadableRecordIsSaidAndTheOtherRecordIsStillFollowed()
    {
        CreateDropboxFolder(Personal);
        WriteRecord(LocalRecord, personal: Personal);
        Write(RoamingRecord, "{ \"personal\": ");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([PersonalCache], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not read", StringComparison.Ordinal)
            && n.Message.Contains(RoamingRecord, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A record Windows will not describe is never read as Dropbox not being set up.</summary>
    [Fact]
    public async Task ARefusedRecordIsReportedAsUnreachedRatherThanAbsent()
    {
        WriteRecord(RoamingRecord, personal: Personal);
        CreateDropboxFolder(Personal);

        using (DeniedDirectory.WithUnreadableFile(RoamingRecord))
        {
            var provider = CreateProvider();
            var plan = await provider.PlanAsync();

            Assert.True(await provider.IsPresentAsync());
            Assert.Empty(plan.Steps);
            Assert.True(plan.WasNotExamined);
            Assert.Contains(plan.Notes, n => n.Message.Contains("Windows would not say", StringComparison.Ordinal));
        }
    }

    /// <summary>A linked cache is never looked through, so §5.6 is not asked of a tree reached through a link.</summary>
    [Fact]
    public async Task ALinkedCacheIsLeftAlone()
    {
        var target = Path.Combine(_temp.Path, "elsewhere", "cache");
        var staged = Write(Path.Combine(target, "staged.bin"));
        Write(Path.Combine(Personal, "Report.docx"));
        Junction.ToDirectory(PersonalCache, target);
        WriteRecord(RoamingRecord, personal: Personal);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(staged));
    }

    /// <summary>A cache Dropbox has just cleared is Dropbox on the machine with nothing to reclaim, said as such.</summary>
    [Fact]
    public async Task AnEmptyCacheSaysItHoldsNothing()
    {
        Directory.CreateDirectory(PersonalCache);
        WriteRecord(RoamingRecord, personal: Personal);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(await provider.IsPresentAsync());
        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("holds nothing", StringComparison.Ordinal));
    }

    /// <summary>A duplicate search must know that each cache is a place this row deletes from.</summary>
    [Fact]
    public async Task EachCacheIsACleanedPlace()
    {
        CreateDropboxFolder(Personal);
        CreateDropboxFolder(Business);
        WriteRecord(RoamingRecord, personal: Personal, business: Business);

        var places = await CreateProvider().CleanedPlacesAsync();

        Assert.Equal(
            new[] { PersonalCache, Path.Combine(Business, ".dropbox.cache") }.Order(StringComparer.OrdinalIgnoreCase),
            places.Select(place => place.Path).Order(StringComparer.OrdinalIgnoreCase));
    }
}
