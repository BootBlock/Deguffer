using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Kopia keeps its connections in the roaming profile and its caches in the local one, and a connection
/// file names its own cache. So these are about finding each cache from the files Kopia writes, and
/// about the connections never being reached (§5.2, §5.6).
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>, with
/// connection files written as Kopia writes them. No Kopia was installed on the machine these were
/// written on, and nothing here runs Kopia.</para>
/// </summary>
public sealed class KopiaCacheProviderTests : IDisposable
{
    private const string CacheId = "0123456789abcdef";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessInspector _inspector = new();

    public KopiaCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private KopiaCacheProvider CreateProvider() => new(_environment, _runner, _inspector, system: _system);

    private string ConfigFolder => Path.Combine(_environment.RoamingAppData, "kopia");

    private string LocalFolder => Path.Combine(_environment.LocalAppData, "kopia");

    private string Cache => Path.Combine(LocalFolder, CacheId);

    private string Connection => Path.Combine(ConfigFolder, "repository.config");

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>A cache folder as Kopia fills one, with the format record and the tag it writes beside the folders.</summary>
    private void CreateCache(string? cache = null)
    {
        foreach (var name in (string[])["contents", "metadata", "indexes", "own-writes"])
        {
            Populate(Path.Combine(cache ?? Cache, name));
        }

        File.WriteAllText(Path.Combine(cache ?? Cache, "kopia.repository"), "{}");
        File.WriteAllText(Path.Combine(cache ?? Cache, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55\n");
    }

    /// <summary>A connection file as Kopia writes it: the cache relative to the file's folder, and the storage's credentials.</summary>
    private string Connect(string recorded, string name = "repository.config")
    {
        Directory.CreateDirectory(ConfigFolder);
        var file = Path.Combine(ConfigFolder, name);
        File.WriteAllText(file, $$"""
            {
              "storage": { "type": "s3", "config": { "bucket": "backups", "accessKeyID": "<YOUR_ACCESS_KEY>" } },
              "caching": { "cacheDirectory": {{System.Text.Json.JsonSerializer.Serialize(recorded)}}, "maxCacheSize": 5242880000 },
              "hostname": "example", "username": "testuser"
            }
            """);
        return file;
    }

    private string Recorded(string cache) => Path.GetRelativePath(ConfigFolder, cache);

    [Fact]
    public async Task ReportsNotPresentWhenKopiaHasNeverCached()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task RemovesOnlyTheFoldersKopiaCachesIntoInTheCacheItsConnectionNames()
    {
        CreateCache();
        Connect(Recorded(Cache));
        var logs = Populate(Path.Combine(LocalFolder, "cli-logs"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(
            ["contents", "indexes", "metadata", "own-writes"],
            plan.TargetedPaths.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(plan.TargetedPaths, path => Assert.Equal(Cache, Path.GetDirectoryName(path)));
        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(ConfigFolder, path) || LongPath.Contains(logs, path));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(ConfigFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Connection, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_runner.Invocations);
    }

    /// <summary>A connection given its own cache folder with --cache-directory records it as a full path.</summary>
    [Fact]
    public async Task FindsACacheAConnectionRecordsElsewhere()
    {
        var elsewhere = Path.Combine(_temp.Path, "caches", "kopia-main");
        CreateCache(elsewhere);
        Connect(elsewhere, "repository-1700000000000.config");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(elsewhere, "contents"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A cache whose connection has gone is still Kopia's, found by the name Kopia gave it.</summary>
    [Fact]
    public async Task FindsACacheWithNoConnectionByItsName()
    {
        CreateCache();

        Assert.True(await CreateProvider().IsPresentAsync());
        Assert.Contains(Path.Combine(Cache, "metadata"), (await CreateProvider().PlanAsync()).TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HonoursTheCacheVariable()
    {
        var moved = Path.Combine(_temp.Path, "caches", "kopia");
        CreateCache(moved);
        Connect(Recorded(Cache));
        _environment.WithEnvironmentVariable(KopiaCacheLayout.CacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(moved, "contents"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>contents</c> and <c>metadata</c> are names anything may use, so a connection naming the profile
    /// as its cache offers nothing there.
    /// </summary>
    [Fact]
    public async Task WillNotTreatTheProfileAsKopiasCache()
    {
        var shared = Populate(Path.Combine(_environment.UserProfile, "contents"));
        Connect(_environment.UserProfile);

        var provider = CreateProvider();
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(shared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as Kopia's", StringComparison.Ordinal));
    }

    /// <summary>§5.2: a folder Kopia does not cache into is Tier 4, left alone and asserted standing.</summary>
    [Fact]
    public async Task AnUnrecognisedFolderInACacheIsLeftStanding()
    {
        CreateCache();
        var stranger = Populate(Path.Combine(Cache, "mine"));

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARecognisedFolderThatIsALinkIsDeclined()
    {
        CreateCache();
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(Cache, "blob-list");
        SymbolicLink.ToDirectory(link, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(link, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("kopia")]
    [InlineData("KopiaUI")]
    public async Task HoldsEverythingBackWhileKopiaRuns(string process)
    {
        CreateCache();
        Connect(Recorded(Cache));
        _inspector.WithRunning(process);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Cache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.6, executed: the folders Kopia caches into go, and the connection, the configuration folder, the
    /// format record, the logs and the stranger are all still standing.
    /// </summary>
    [Fact]
    public async Task AClean_LeavesTheConnectionsAndEverythingUnrecognisedStanding()
    {
        CreateCache();
        Connect(Recorded(Cache));
        var logs = Populate(Path.Combine(LocalFolder, "cli-logs"));
        var stranger = Populate(Path.Combine(Cache, "mine"));

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Succeeded);
        Assert.Equal(4 * 4096, result.BytesReclaimed);
        Assert.False(Directory.Exists(Path.Combine(Cache, "contents")));
        Assert.True(File.Exists(Connection));
        Assert.True(File.Exists(Path.Combine(Cache, "kopia.repository")));
        Assert.True(File.Exists(Path.Combine(logs, "payload.bin")));
        Assert.True(File.Exists(Path.Combine(stranger, "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task AClean_RemovesNothingWhenKopiaStartedAfterThePreview()
    {
        CreateCache();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _inspector.WithRunning("KopiaUI").PassTheReadingLimit();

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Path.Combine(Cache, "contents")));
        Assert.Equal(0, result.BytesReclaimed);
    }

    /// <summary>
    /// A folder Windows will not describe is not an empty one: a cache may be in it, so the row appears and
    /// says it could not look, rather than report that Kopia cached nothing.
    /// </summary>
    [Fact]
    public async Task AKopiaFolderWindowsWillNotDescribeIsReportedRatherThanReadAsEmpty()
    {
        CreateCache();
        using var denied = DeniedDirectory.WithUnreadableAttributes(LocalFolder);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.HasUnreadableRoot);
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("cached nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A connection the command line keeps elsewhere still has its password beside it, and both are refused
    /// in Explore and asserted to survive. The rest of that folder is the user's, and stays open.
    /// </summary>
    [Fact]
    public async Task AConnectionKeptElsewhereIsRefusedWithItsPassword()
    {
        CreateCache();
        var folder = Path.Combine(_temp.Path, "backup-settings");
        Directory.CreateDirectory(folder);
        var connection = Path.Combine(folder, "nas.config");
        File.WriteAllText(connection, $$"""{ "caching": { "cacheDirectory": {{System.Text.Json.JsonSerializer.Serialize(Cache)}} } }""");
        var password = connection + ".kopia-password";
        File.WriteAllText(password, "<YOUR_PASSWORD>");
        var mine = Path.Combine(folder, "notes.txt");
        File.WriteAllText(mine, "mine");
        _environment.WithEnvironmentVariable(KopiaCacheLayout.ConfigVariable, connection);

        var provider = CreateProvider();
        var policy = new ExploreActionPolicy(
            [], provider.ToolRoots, new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.False(policy.MayRemove(connection).IsAllowed);
        Assert.False(policy.MayRemove(password).IsAllowed);
        Assert.True(policy.MayRemove(mine).IsAllowed);

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(connection, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(password, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateCache();
        Connect(Recorded(Cache));

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData(@"Roaming\kopia", false)]
    [InlineData(@"Roaming\kopia\repository.config", false)]
    [InlineData(@"Roaming\kopia\repository.config.kopia-password", false)]
    [InlineData(@"Local\kopia", false)]
    [InlineData(@"Local\kopia\cli-logs", false)]
    [InlineData(@"Local\kopia\" + CacheId, false)]
    [InlineData(@"Local\kopia\" + CacheId + @"\kopia.repository", false)]
    [InlineData(@"Local\kopia\" + CacheId + @"\mine", false)]
    [InlineData(@"Local\kopia\" + CacheId + @"\contents", true)]
    [InlineData(@"Local\kopia\" + CacheId + @"\server-contents", true)]
    public async Task ExploreMayRemoveOnlyWhatKopiaCaches(string relative, bool allowed)
    {
        CreateCache();
        Connect(Recorded(Cache));
        Directory.CreateDirectory(Path.Combine(Cache, "mine"));

        var provider = CreateProvider();
        var policy = new ExploreActionPolicy(
            [], provider.ToolRoots, new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        var appData = Path.GetDirectoryName(_environment.LocalAppData)!;

        Assert.Equal(allowed, policy.MayRemove(Path.Combine(appData, relative)).IsAllowed);
    }
}
