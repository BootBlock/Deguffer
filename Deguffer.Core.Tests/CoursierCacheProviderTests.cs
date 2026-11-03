using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Coursier keeps its downloads in <c>v1</c> and the Java runtimes it installs in <c>arc</c> beside
/// it, so these are mostly negative tests: a rule one folder too broad uninstalls the user's Java.
/// </summary>
public sealed class CoursierCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public CoursierCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string CacheHome => Path.Combine(_environment.LocalAppData, "Coursier", "Cache");

    private string ArtefactCache => Path.Combine(CacheHome, "v1");

    private CoursierCacheProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    [Fact]
    public async Task ReportsNotPresentWhenCoursierNeverRan()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansTheDownloadsInTheArtefactCacheAndNeverTheCacheItself()
    {
        var https = CreateAt(ArtefactCache, @"https\repo1.maven.org\maven2", 4096);
        var http = CreateAt(ArtefactCache, @"http\repo.example.test", 2048);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            [Path.Combine(ArtefactCache, "http"), Path.Combine(ArtefactCache, "https")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(ArtefactCache, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.True(Directory.Exists(https) && Directory.Exists(http));
    }

    /// <summary>
    /// §5.2. Each sibling of the artefact cache holds something no download brings back: the Java
    /// runtimes <c>JAVA_HOME</c> points into, or container layers built on this computer. None is ever
    /// targeted, and each is asserted to survive (§5.6) under what it holds.
    /// </summary>
    [Theory]
    [InlineData("arc", "Java runtimes")]
    [InlineData("jvm", "Java runtimes")]
    [InlineData("digest", "cs docker build")]
    [InlineData("priv", "running container")]
    public async Task NeverPlansACacheBesideTheDownloads(string sibling, string reason)
    {
        CreateAt(ArtefactCache, "https", 4096);
        var kept = CreateAt(CacheHome, sibling, 8192);

        var plan = await CreateProvider().PlanAsync();

        Assert.All(plan.TargetedPaths, path =>
            Assert.False(LongPath.Contains(path, kept) || LongPath.Contains(kept, path), $"{path} overlaps {kept}."));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present
            && p.Reason.Contains(reason, StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.Contains($"'{sibling}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutingRemovesTheDownloadsAndLeavesTheJavaRuntimesStanding()
    {
        CreateAt(ArtefactCache, @"https\repo1.maven.org", 4096);
        CreateAt(ArtefactCache, "http", 4096);
        var java = InstallJdk(Path.Combine(CacheHome, "arc"));
        var legacyJdk = CreateAt(CacheHome, @"jvm\temurin@17", 4096);
        var installed = CreateAt(_environment.LocalAppData, @"Coursier\data\bin", 1024);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(ArtefactCache, "https")));
        Assert.False(Directory.Exists(Path.Combine(ArtefactCache, "http")));

        Assert.True(Directory.Exists(ArtefactCache));
        Assert.True(File.Exists(java));
        Assert.True(Directory.Exists(legacyJdk));
        Assert.True(Directory.Exists(installed));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheArchiveCacheVanished()
    {
        CreateAt(ArtefactCache, "https", 1024);
        InstallJdk(Path.Combine(CacheHome, "arc"));
        var arc = Path.Combine(CacheHome, "arc");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // The over-broad rule §5.6 exists to catch: the whole Cache folder treated as the cache.
        Directory.Delete(arc, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(arc, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnUnrecognisedChildOfTheArtefactCacheIsTier4AndProtected()
    {
        CreateAt(ArtefactCache, "https", 1024);
        var unknown = CreateAt(ArtefactCache, "something-a-later-coursier-added", 8192);

        Assert.Equal(SafetyTier.DoNotTouch, CoursierCacheProvider.ArtefactChildren.Classify("something-a-later-coursier-added").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(unknown, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task PlansTheDownloadsWhereTheVariableMovedTheArtefactCache()
    {
        var moved = Path.Combine(_temp.Path, "drive-d", "coursier");
        CreateAt(moved, "https", 4096);
        var stale = CreateAt(ArtefactCache, "https", 4096);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, moved);

        var provider = CreateProvider();

        Assert.Equal(moved, provider.ResolveArtefactCache());

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "https")], plan.TargetedPaths);
        Assert.DoesNotContain(stale, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.6 for a kept cache its own variable moved: it is asserted where Coursier now keeps it.</summary>
    [Fact]
    public async Task ProtectsAnArchiveCacheItsVariableMoved()
    {
        CreateAt(ArtefactCache, "https", 4096);
        var moved = Path.Combine(_temp.Path, "drive-d", "coursier-arc");
        InstallJdk(moved);
        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// A variable that puts the archive cache inside a download folder makes the address layout no
    /// evidence of a download. That folder is spared whole, and the other is still offered.
    /// </summary>
    [Fact]
    public async Task SparesADownloadFolderThatHoldsAKeptCache()
    {
        CreateAt(ArtefactCache, "http", 4096);
        var https = Path.Combine(ArtefactCache, "https");
        var java = InstallJdk(Path.Combine(https, "runtimes"));
        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", Path.Combine(https, "runtimes"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(ArtefactCache, "http")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(https, StringComparison.OrdinalIgnoreCase));

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(java));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The archive cache is laid out by address as well, so a variable naming it as the artefact cache
    /// would read its <c>https</c> as downloads. The JDKs inside are spared.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhereTheVariableNamesTheArchiveCacheAsTheArtefactCache()
    {
        var arc = Path.Combine(CacheHome, "arc");
        var java = InstallJdk(arc);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, arc);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(arc, "https"), StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(java));
    }

    /// <summary>
    /// The variable naming the folder around every cache makes each kept cache a child of the walk. It
    /// is protected under what it holds, and said once.
    /// </summary>
    [Fact]
    public async Task NamesAKeptCacheOnceWhereTheVariableNamesTheFolderAroundIt()
    {
        CreateAt(CacheHome, "https", 4096);
        InstallJdk(Path.Combine(CacheHome, "arc"));
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, CacheHome);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(CacheHome, "https")], plan.TargetedPaths);
        Assert.Single(plan.Notes, n => n.Message.Contains("'arc'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(CacheHome, "arc"), StringComparison.OrdinalIgnoreCase)
            && p.Reason.Contains("Java runtimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WillNotTreatADriveRootAsCoursiersCache()
    {
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, @"Q:\");

        var provider = CreateProvider();

        Assert.Null(provider.ResolveArtefactCache());
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("root of a drive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersNothingWhenTheVariableIsRelative()
    {
        CreateAt(ArtefactCache, "https", 4096);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, @"cache\coursier");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains(ConfiguredFolder.NotAFullPath, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DeclinesAnArtefactCacheThatIsItselfALink(DirectoryLinkKind kind)
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = CreateAt(outside, "https", 4096);
        Directory.CreateDirectory(CacheHome);
        DirectoryLink.Create(kind, ArtefactCache, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task WarnsWhenAJavaProcessMayBeReadingTheCache()
    {
        CreateAt(ArtefactCache, "https", 1024);

        var plan = await CreateProvider(new FakeProcessInspector("java")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateAt(ArtefactCache, "https", 4096);
        CreateAt(ArtefactCache, "http", 4096);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleansWhereTheVariableMovedTheCache()
    {
        var moved = Path.Combine(_temp.Path, "drive-d", "coursier");
        CreateAt(moved, "https", 4096);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, moved);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    /// <summary>
    /// §7.1. Explore applies §5.2 to a folder the user picks, so the Java runtimes and the
    /// applications <c>cs install</c> put on the path are refused there, and only the downloads are not.
    /// </summary>
    [Theory]
    [InlineData(@"Cache\arc", false)]
    [InlineData(@"Cache\arc\https\github.com", false)]
    [InlineData(@"Cache\jvm", false)]
    [InlineData(@"Cache\digest", false)]
    [InlineData(@"Cache\v1", false)]
    [InlineData(@"Cache\v1\unexpected", false)]
    [InlineData(@"data\bin", false)]
    [InlineData(@"Cache", false)]
    [InlineData(@"Cache\v1\https", true)]
    [InlineData(@"Cache\v1\https\repo1.maven.org", true)]
    public void ExploreRefusesEverythingInCoursiersFolderButTheDownloads(string relative, bool allowed)
    {
        var provider = CreateProvider();
        var path = Path.Combine(provider.CoursierHome, relative);
        Directory.CreateDirectory(path);

        Assert.Equal(allowed, Policy(provider).MayRemove(path).IsAllowed);
    }

    [Fact]
    public void ExploreRefusesAnArchiveCacheItsVariableMoved()
    {
        // In the profile, where Explore permits removal, so the refusal can only be Coursier's.
        var moved = Path.Combine(_environment.UserProfile, "coursier-arc");
        var java = InstallJdk(moved);

        Assert.True(Policy(CreateProvider()).MayRemove(moved).IsAllowed);

        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", moved);

        var policy = Policy(CreateProvider());

        Assert.False(policy.MayRemove(moved).IsAllowed);
        Assert.False(policy.MayRemove(Path.GetDirectoryName(java)!).IsAllowed);
    }

    /// <summary>
    /// The plan spares the archive cache's <c>https</c> where the variable names the archive cache as the
    /// artefact cache. Explore must refuse it there too, or the user can pick the JDKs out of the map.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheJavaRuntimesWhereTheVariableNamesTheArchiveCacheAsTheArtefactCache()
    {
        var arc = Path.Combine(CacheHome, "arc");
        var java = InstallJdk(arc);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, arc);

        var policy = await WiredPolicyAsync(CreateProvider());

        Assert.False(policy.MayRemove(Path.Combine(arc, "https")).IsAllowed);
        Assert.False(policy.MayRemove(Path.GetDirectoryName(java)!).IsAllowed);
    }

    /// <summary>
    /// Two variables naming one folder put the artefact cache's root and the archive cache's refusal at
    /// the same place, where a root that recognises a child would lift the other's refusal.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheJavaRuntimesWhereBothVariablesNameOneFolder()
    {
        var shared = Path.Combine(_environment.UserProfile, "coursier");
        var java = InstallJdk(shared);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, shared);
        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", shared);

        var provider = CreateProvider();
        var policy = await WiredPolicyAsync(provider);

        Assert.False(policy.MayRemove(Path.Combine(shared, "https")).IsAllowed);
        Assert.True((await provider.PlanAsync()).TargetedPaths.Count == 0);
        Assert.True(File.Exists(java));
    }

    /// <summary>
    /// §7.4. A duplicate search keeps no copy inside a place the next clean removes, so a download folder
    /// the plan spares must not be declared cleaned, or the only copy of a JDK file reads as disposable.
    /// </summary>
    [Fact]
    public async Task CleanedPlacesLeaveOutADownloadFolderThatOverlapsAKeptCache()
    {
        var arc = Path.Combine(CacheHome, "arc");
        InstallJdk(arc);
        _environment.WithEnvironmentVariable(CoursierCacheProvider.CacheVariable, arc);

        var places = await CreateProvider().CleanedPlacesAsync();

        Assert.DoesNotContain(places, place => LongPath.Contains(arc, place.Path));
        Assert.Contains(places, place => place.Path.Equals(Path.Combine(ArtefactCache, "https"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A kept cache moved to a folder this declines, such as Documents, still holds Java runtimes. Explore
    /// refuses what Coursier fills there and nothing else, so the user's own folders stay theirs.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesOnlyCoursiersEntriesWhereAKeptCacheVariableNamesTheAccountsDocuments()
    {
        var documents = _environment.Documents!;
        var java = InstallJdk(documents);
        var theirs = Directory.CreateDirectory(Path.Combine(documents, "letters")).FullName;
        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", documents);

        var provider = CreateProvider();

        Assert.DoesNotContain(provider.ToolRoots, root => root.Path.Equals(documents, StringComparison.OrdinalIgnoreCase));

        var policy = await WiredPolicyAsync(provider);

        Assert.False(policy.MayRemove(Path.Combine(documents, "https")).IsAllowed);
        Assert.False(policy.MayRemove(Path.GetDirectoryName(java)!).IsAllowed);
        Assert.True(policy.MayRemove(theirs).IsAllowed);
    }

    /// <summary>
    /// §5.2 through a substituted letter. The archive cache named through <c>S:</c> is inside the
    /// artefact cache's <c>https</c>, which no comparison of the two paths' text can see.
    /// </summary>
    [Fact]
    public async Task SparesADownloadFolderThatHoldsAKeptCacheNamedThroughASubstitutedLetter()
    {
        CreateAt(ArtefactCache, "http", 4096);
        var https = Path.Combine(ArtefactCache, "https");
        var java = InstallJdk(Path.Combine(https, "runtimes"));
        var volumes = new FakeVolumeInventory().Substituting(@"S:\", _environment.LocalAppData);
        _environment.WithEnvironmentVariable("COURSIER_ARCHIVE_CACHE", @"S:\Coursier\Cache\v1\https\runtimes");

        var provider = new CoursierCacheProvider(
            _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, system: _system, volumes: volumes);
        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(ArtefactCache, "http")], plan.TargetedPaths);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(java));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>The policy as the shell builds it, from what the provider declares and discovers.</summary>
    private Task<ExploreActionPolicy> WiredPolicyAsync(CoursierCacheProvider provider) =>
        ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

    private ExploreActionPolicy Policy(CoursierCacheProvider provider) =>
        new(ProtectedRegions.For(_system, _environment), provider.ToolRoots, volumes: new FakeVolumeInventory());

    /// <summary>A Java runtime laid out as <c>cs java</c> extracts one into the archive cache.</summary>
    private static string InstallJdk(string archiveCache)
    {
        var bin = Path.Combine(archiveCache, "https", "github.com", "temurin17", "jdk17.zip", "jdk-17", "bin");
        Directory.CreateDirectory(bin);
        var java = Path.Combine(bin, "java.exe");
        File.WriteAllBytes(java, new byte[2048]);
        return java;
    }

    /// <summary>Create <paramref name="child"/> under the root holding one file of the given size.</summary>
    private static string CreateAt(string root, string child, int bytes)
    {
        var directory = Path.Combine(root, child);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }
}
