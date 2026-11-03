using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// restic's cache folder holds one folder a repository, named by the repository's identifier, so these
/// are about which route clears them and about what neither route may take (§5.1, §5.2, §5.6).
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>, and
/// restic's cleanup is a fake that removes what the real one removes: every folder in the cache folder
/// named as a repository's cache. No restic was installed on the machine these were written on.</para>
/// </summary>
public sealed class ResticCacheProviderTests : IDisposable
{
    private const string RepositoryId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessInspector _inspector = new();

    public ResticCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private ResticCacheProvider CreateProvider() => new(_environment, _runner, _inspector, system: _system);

    private string Root => Path.Combine(_environment.LocalAppData, "restic");

    private string Repository => Path.Combine(Root, RepositoryId);

    private string Restic => _environment.FindExecutable("restic")!;

    private string CleanupArguments(string root) => $"--cache-dir \"{root}\" cache --cleanup --max-age 0";

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>A repository's cache as restic writes one, and the tag restic puts beside it.</summary>
    private void CreateCache(string? root = null, string id = RepositoryId)
    {
        var repository = Path.Combine(root ?? Root, id);

        foreach (var name in (string[])["data", "index", "snapshots"])
        {
            Populate(Path.Combine(repository, name));
        }

        File.WriteAllText(Path.Combine(repository, "version"), "1");
        File.WriteAllText(Path.Combine(root ?? Root, "CACHEDIR.TAG"), "Signature: 8a477f597d28d172789f06886806bc55\n");
    }

    /// <summary>restic installed, with a cleanup that removes every repository's cache in the folder it is given.</summary>
    private void ResticThatCleansUp()
    {
        _environment.WithExecutable("restic");

        _runner.Replying(Restic, arguments =>
        {
            var root = arguments.Split('"')[1];

            foreach (var folder in Directory.EnumerateDirectories(root).Where(f => ResticCacheLayout.IsTakenByCleanup(Path.GetFileName(f))).ToList())
            {
                Directory.Delete(folder, recursive: true);
            }

            return new CommandOutcome(0, "remove 1 old cache directories", string.Empty);
        });
    }

    [Fact]
    public async Task ReportsNotPresentWhenResticHasNeverCached()
    {
        Directory.CreateDirectory(Root);
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task ClearsTheCacheWithResticsOwnCleanupSentToTheFolderByName()
    {
        CreateCache();
        _environment.WithExecutable("restic");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        var cleanup = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(Restic, cleanup.FileName);
        Assert.Equal(CleanupArguments(Root), cleanup.Arguments);
        Assert.Equal([Repository], cleanup.MeasuredPaths);
        Assert.Equal(3 * 4096 + 1, cleanup.EstimatedBytes); // the version file goes with the cache
        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Root, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Root, "CACHEDIR.TAG"), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_runner.Invocations);
    }

    [Fact]
    public async Task RemovesTheCachedFoldersByPathWhenResticCannotBeFound()
    {
        CreateCache();

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(
            [Path.Combine(Repository, "data"), Path.Combine(Repository, "index"), Path.Combine(Repository, "snapshots")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(Repository, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Repository, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Path.Combine(Repository, "version"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("cannot find restic", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2: restic's cleanup removes a repository's cache whole, so a name restic does not write there
    /// sends the plan down the path route, where the stranger is Tier 4 and asserted standing.
    /// </summary>
    [Fact]
    public async Task AStrangerInARepositoryCacheStopsTheCleanupAndIsLeftStanding()
    {
        CreateCache();
        var stranger = Populate(Path.Combine(Repository, "keys"));
        _environment.WithExecutable("restic");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal(3, plan.TargetedPaths.Count);
        Assert.DoesNotContain(stranger, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Message.Contains("not something restic writes", StringComparison.Ordinal));
    }

    /// <summary>restic's cleanup would take a cache 'restic check' made, which Deguffer does not offer.</summary>
    [Fact]
    public async Task ACheckCacheStopsTheCleanupAndIsLeftStanding()
    {
        CreateCache();
        var check = Populate(Path.Combine(Root, "restic-check-cache-1234"));
        _environment.WithExecutable("restic");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain(check, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(check, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A folder whose name is one digit short of an identifier is not a repository's cache.</summary>
    [Fact]
    public async Task AFolderNotNamedAsResticNamesARepositoryIsTierFour()
    {
        var near = Populate(Path.Combine(Root, RepositoryId[1..], "data"));

        var provider = CreateProvider();
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(Path.GetDirectoryName(near)!, path));
    }

    /// <summary>
    /// §5.6, executed through restic's command: the repository's cache goes, and the cache folder, the
    /// tag and an unrecognised file beside them are all still standing.
    /// </summary>
    [Fact]
    public async Task AClean_RunsTheCleanupAndLeavesTheFolderAndTheUnrecognisedStanding()
    {
        CreateCache();
        var notes = Path.Combine(Root, "notes.txt");
        File.WriteAllText(notes, "mine");
        ResticThatCleansUp();

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        var run = Assert.Single(_runner.Invocations);
        Assert.Equal(CleanupArguments(Root), run.Arguments);
        Assert.True(result.Succeeded);
        Assert.Equal(3 * 4096 + 1, result.BytesReclaimed);
        Assert.False(Directory.Exists(Repository));
        Assert.True(File.Exists(Path.Combine(Root, "CACHEDIR.TAG")));
        Assert.True(File.Exists(notes));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>§5.6, executed by path: only the three folders go, and the stranger and the version file stay.</summary>
    [Fact]
    public async Task AClean_ByPathLeavesTheVersionFileAndTheStrangerStanding()
    {
        CreateCache();
        var stranger = Populate(Path.Combine(Repository, "keys"));

        var provider = CreateProvider();
        var result = await provider.ExecuteAsync(await provider.PlanAsync());

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(Repository, "data")));
        Assert.False(Directory.Exists(Path.Combine(Repository, "snapshots")));
        Assert.True(File.Exists(Path.Combine(Repository, "version")));
        Assert.True(File.Exists(Path.Combine(stranger, "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// restic decides what to remove when it runs, so a stranger put into a repository's cache after the
    /// preview stops the cleanup, and the stranger survives.
    /// </summary>
    [Fact]
    public async Task AClean_DoesNotRunTheCleanupWhenAStrangerArrivedAfterThePreview()
    {
        CreateCache();
        ResticThatCleansUp();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        Assert.Single(plan.Steps.OfType<RunCommandStep>());

        var arrived = Populate(Path.Combine(Path.Combine(Root, "f" + RepositoryId[1..]), "mine"));

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_runner.Invocations);
        Assert.True(File.Exists(Path.Combine(arrived, "payload.bin")));
        Assert.Contains(result.Steps, step =>
            !step.Succeeded && step.Message!.Contains("restic's cleanup would remove it with the rest", StringComparison.Ordinal));
    }

    /// <summary>A link where a repository's cache was would be cleaned through by restic, so the cleanup is not sent.</summary>
    [Fact]
    public async Task ALinkNamedAsARepositoryIsDeclinedAndNotCleanedThrough()
    {
        _environment.WithExecutable("restic");
        CreateCache();
        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        var link = Path.Combine(Root, "a" + RepositoryId[1..]);
        SymbolicLink.ToDirectory(link, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps.OfType<RunCommandStep>());
        Assert.DoesNotContain(plan.TargetedPaths, path => LongPath.Contains(link, path));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(link, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Nothing while restic runs, by either route, and the clean asks again before it removes.</summary>
    [Fact]
    public async Task HoldsEverythingBackWhileResticRuns()
    {
        CreateCache();
        _environment.WithExecutable("restic");
        _inspector.WithRunning("restic");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Repository, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task AClean_DoesNotRunTheCleanupWhenResticStartedAfterThePreview()
    {
        CreateCache();
        ResticThatCleansUp();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        _inspector.WithRunning("restic");

        var result = await provider.ExecuteAsync(plan);

        Assert.Empty(_runner.Invocations);
        Assert.True(Directory.Exists(Path.Combine(Repository, "data")));
        Assert.Contains(result.Steps, step => !step.Succeeded && step.Message!.Contains("restic is running now", StringComparison.Ordinal));
    }

    /// <summary>The folder the variable names is cleaned, and so is the default, which still holds older caches.</summary>
    [Fact]
    public async Task HonoursTheCacheVariableAndStillCleansTheDefault()
    {
        var moved = Path.Combine(_temp.Path, "caches", "restic");
        CreateCache(moved);
        CreateCache();
        _environment.WithExecutable("restic");
        _environment.WithEnvironmentVariable(ResticCacheLayout.CacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            new[] { CleanupArguments(Root), CleanupArguments(moved) }.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            plan.Steps.OfType<RunCommandStep>().Select(step => step.Arguments).Order(StringComparer.OrdinalIgnoreCase).ToList());
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WillNotTreatTheProfileAsResticsCacheFolder()
    {
        CreateCache(_environment.UserProfile);
        _environment.WithExecutable("restic");
        _environment.WithEnvironmentVariable(ResticCacheLayout.CacheVariable, _environment.UserProfile);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.DoesNotContain(provider.ToolRoots, root => root.Path.Equals(_environment.UserProfile, StringComparison.OrdinalIgnoreCase));

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as restic's cache folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeclinesACacheFolderThatIsItselfALink()
    {
        _environment.WithExecutable("restic");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        CreateCache(outside);
        SymbolicLink.ToDirectory(Root, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.True(Directory.Exists(Path.Combine(outside, RepositoryId, "data")));
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleansByEitherRoute()
    {
        CreateCache();

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));

        _environment.WithExecutable("restic");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("CACHEDIR.TAG", false)]
    [InlineData("restic-check-cache-1", false)]
    [InlineData(RepositoryId, false)]
    [InlineData(RepositoryId + @"\version", false)]
    [InlineData(RepositoryId + @"\keys", false)]
    [InlineData(RepositoryId + @"\data", true)]
    [InlineData(RepositoryId + @"\index", true)]
    [InlineData(RepositoryId + @"\snapshots", true)]
    public async Task ExploreMayRemoveOnlyWhatResticCaches(string relative, bool allowed)
    {
        CreateCache();
        Directory.CreateDirectory(Path.Combine(Root, "restic-check-cache-1"));
        Directory.CreateDirectory(Path.Combine(Repository, "keys"));

        var provider = CreateProvider();
        var policy = new ExploreActionPolicy(
            [], provider.ToolRoots, new FakeVolumeInventory(), probedRoots: await provider.DiscoverToolRootsAsync());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Root : Path.Combine(Root, relative)).IsAllowed);
    }
}
