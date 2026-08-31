using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// vcpkg is the first tool Deguffer reaches whose main directory is a git clone the user put
/// wherever they liked, so two things have to be shown that no earlier provider needed: that each of
/// the three routes to finding it works, and that a machine offering none of them produces a plan
/// which says out loud what it could not see rather than a quietly smaller number.
///
/// The §5.2 hazard is also larger than usual. <c>installed</c> sits in the same root as the three
/// scratch directories, it is refilled by exactly the command that fills them, and every project on
/// the machine links against it.
/// </summary>
public sealed class VcpkgCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VcpkgCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VcpkgCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string ProfileDirectory => Path.Combine(_environment.LocalAppData, "vcpkg");

    private string DefaultBinaryCache => Path.Combine(ProfileDirectory, "archives");

    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>
    /// A clone with all three scratch directories, the payload that must survive, and the marker
    /// file vcpkg's own tooling identifies a root by.
    /// </summary>
    private string CreateClone(string? at = null)
    {
        var root = at ?? Path.Combine(_temp.Path, "dev", "vcpkg");

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, VcpkgDiscovery.RootMarker), string.Empty);

        Populate(Path.Combine(root, "buildtrees"));
        Populate(Path.Combine(root, "downloads"));
        Populate(Path.Combine(root, "packages"));
        Populate(Path.Combine(root, "installed"));
        Populate(Path.Combine(root, "ports"));

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenVcpkgHasCachedNothing()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The user's vcpkg directory exists on any machine that has ever integrated with Visual Studio,
    /// so it existing must not read as presence.
    /// </summary>
    [Fact]
    public async Task TheProfileDirectoryExistingIsNotPresence()
    {
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(
            Path.Combine(ProfileDirectory, VcpkgDiscovery.IntegrationFile),
            Path.Combine(_temp.Path, "dev", "vcpkg"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansTheBinaryCacheAndTheThreeScratchDirectoriesUnderTheClone()
    {
        Populate(DefaultBinaryCache);
        var root = CreateClone();
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        string[] expected =
        [
            DefaultBinaryCache,
            Path.Combine(root, "buildtrees"),
            Path.Combine(root, "downloads"),
            Path.Combine(root, "packages"),
        ];

        Assert.Equal(
            expected.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2's trap here is the largest directory of the lot, and it is never enumerated and never a
    /// target. Naming it is what turns "we did not target it" into evidence.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheCloneOrTheLibrariesInstalledFromIt()
    {
        var root = CreateClone();
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var plan = await CreateProvider().PlanAsync();

        foreach (var name in (string[])["installed", "ports"])
        {
            var path = Path.Combine(root, name);

            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.All(plan.TargetedPaths, targeted => Assert.False(
                IsAtOrUnder(path, targeted), $"{targeted} would have taken {name} with it."));

            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// A provider that silently reported a fraction of a cache would be worse than one that names
    /// the part it could not reach, and this is the machine where that happens: the binary cache is
    /// in the profile, and nothing on disk says where the clone is.
    /// </summary>
    [Fact]
    public async Task SaysPlainlyWhenItCouldNotFindTheClone()
    {
        Populate(DefaultBinaryCache);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([DefaultBinaryCache], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("binary cache only", StringComparison.Ordinal)
            && n.Message.Contains(VcpkgDiscovery.RootVariable, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotClaimAMissingCloneWhenItFoundOne()
    {
        Populate(DefaultBinaryCache);
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, CreateClone());

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("binary cache only", StringComparison.Ordinal));
    }

    /// <summary>
    /// The second of the three routes: the file <c>vcpkg integrate install</c> wrote, which is the
    /// only record of the clone's location that exists on disk.
    /// </summary>
    [Fact]
    public async Task FindsTheCloneThroughTheFileVcpkgWroteWhenItIntegrated()
    {
        var root = CreateClone();
        Directory.CreateDirectory(ProfileDirectory);
        File.WriteAllText(Path.Combine(ProfileDirectory, VcpkgDiscovery.IntegrationFile), root + "\r\n");

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(root, "buildtrees"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The third and weakest route: the ordinary bootstrap builds the executable into the clone, so
    /// the directory holding it is the root.
    /// </summary>
    [Fact]
    public async Task FindsTheCloneThroughTheExecutableOnPath()
    {
        var root = CreateClone();
        _environment.WithExecutable("vcpkg", Path.Combine(root, "vcpkg.exe"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(root, "packages"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recognising a name is not the same as establishing what a directory is. Without vcpkg's own
    /// marker, a <c>vcpkg.exe</c> copied into the profile would have this provider declare
    /// <c>downloads</c>, <c>packages</c> and <c>buildtrees</c> under it — and <c>Downloads</c> is a
    /// folder most machines have and nobody wants removed.
    /// </summary>
    [Fact]
    public async Task DoesNotTreatADirectoryWithoutTheMarkerAsTheClone()
    {
        var downloads = Populate(Path.Combine(_environment.UserProfile, "downloads"));
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, _environment.UserProfile)
            .WithExecutable("vcpkg", Path.Combine(_environment.UserProfile, "vcpkg.exe"));

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(downloads, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.IsEmpty);
    }

    /// <summary>
    /// The three locations move independently and nothing stops two of them arriving at one
    /// directory. Declared twice it would become two steps over one path: its size counted twice in
    /// the total the user reads, and §5.6 reporting one survivor as two.
    /// </summary>
    [Fact]
    public async Task DeclaresOneDirectoryOnceWhenTwoVariablesNameIt()
    {
        var root = CreateClone();
        var shared = Path.Combine(root, "downloads");
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, shared);

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.TargetedPaths, p => p.Equals(shared, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The documented search order falls through to the roaming profile, and a cache found there
    /// still has that directory's own records sitting beside it.
    /// </summary>
    [Fact]
    public async Task AssertsTheRoamingProfilesRecordsSurviveWhenTheCacheIsFoundThere()
    {
        var roamingProfile = Path.Combine(_environment.RoamingAppData, "vcpkg");
        Populate(Path.Combine(roamingProfile, "archives"));
        Populate(Path.Combine(roamingProfile, "registries"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(roamingProfile, "registries"), StringComparison.OrdinalIgnoreCase)
            && p.ExistedBefore);
    }

    [Fact]
    public async Task HonoursTheConfiguredBinaryCacheLocation()
    {
        var moved = Populate(Path.Combine(_temp.Path, "shared", "vcpkg-archives"));
        Populate(DefaultBinaryCache);
        _environment.WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);
    }

    /// <summary>The documented search order falls through to the roaming profile.</summary>
    [Fact]
    public async Task FindsTheBinaryCacheUnderTheRoamingProfileWhenTheLocalOneIsAbsent()
    {
        var roaming = Populate(Path.Combine(_environment.RoamingAppData, "vcpkg", "archives"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([roaming], plan.TargetedPaths);
    }

    [Fact]
    public async Task HonoursARelocatedDownloadsDirectory()
    {
        var root = CreateClone();
        var moved = Populate(Path.Combine(_temp.Path, "shared", "vcpkg-downloads"));
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.DownloadsVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(moved, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(root, "downloads"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A variable set to the place vcpkg would have used anyway must not declare the same directory
    /// twice — that would be two steps over one path, and §5.6 reporting one survivor as two.
    /// </summary>
    [Fact]
    public async Task DoesNotDeclareTheDownloadsDirectoryTwiceWhenTheVariableAgreesWithTheDefault()
    {
        var root = CreateClone();
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.DownloadsVariable, Path.Combine(root, "downloads"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Single(plan.TargetedPaths, p =>
            p.Equals(Path.Combine(root, "downloads"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// vcpkg resolves a relative value against a working directory Deguffer is not, so a relative
    /// one is no answer at all and the next route is tried.
    /// </summary>
    [Fact]
    public async Task IgnoresARelativeRootAndFallsThroughToTheNextRoute()
    {
        var root = CreateClone();
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, @"..\vcpkg")
            .WithExecutable("vcpkg", Path.Combine(root, "vcpkg.exe"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(root, "packages"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclinesADeclaredDirectoryThatIsALink()
    {
        var root = CreateClone();
        Directory.Delete(Path.Combine(root, "buildtrees"), recursive: true);

        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(root, "buildtrees"), outside);
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(Path.Combine(root, "buildtrees"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.True(Directory.Exists(outside));
    }

    /// <summary>
    /// §6.3. A vcpkg buildtree carries a port's whole source tree under a triplet directory, which
    /// is where <c>MAX_PATH</c> is met in practice, and a truncation there is a silent partial
    /// deletion.
    /// </summary>
    [Fact]
    public async Task ReachesABuildTreeBeyondMaxPath()
    {
        var root = Path.Combine(_temp.Path, "dev", "vcpkg");
        var buildtrees = Path.Combine(root, "buildtrees");
        Directory.CreateDirectory(buildtrees);
        File.WriteAllText(Path.Combine(root, VcpkgDiscovery.RootMarker), string.Empty);
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var deep = buildtrees;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('b', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "object.obj")), new byte[8192]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(8192, plan.EstimatedBytes);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(8192, result.BytesReclaimed);
        Assert.False(Directory.Exists(LongPath.Extended(deep)));
    }

    /// <summary>
    /// §5.6, executed rather than asserted on paper. The unnamed neighbour matters as much as the
    /// named ones: nothing in this provider mentions <c>toolsrc</c>, and only running a plan shows
    /// that a rule reaching into the clone did not take it.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesTheScratchAndLeavesTheCloneStanding()
    {
        Populate(DefaultBinaryCache);
        var root = CreateClone();
        var stray = Populate(Path.Combine(root, "toolsrc"));
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);

        Assert.False(Directory.Exists(Path.Combine(root, "buildtrees")));
        Assert.False(Directory.Exists(Path.Combine(root, "downloads")));
        Assert.False(Directory.Exists(Path.Combine(root, "packages")));
        Assert.False(Directory.Exists(DefaultBinaryCache));

        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(Path.Combine(root, "installed")));
        Assert.True(Directory.Exists(Path.Combine(root, "ports")));
        Assert.True(Directory.Exists(stray));
        Assert.True(Directory.Exists(ProfileDirectory));

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheInstalledLibrariesVanished()
    {
        var root = CreateClone();
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        var installed = Path.Combine(root, "installed");
        Directory.Delete(installed, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(installed, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WarnsWhenVcpkgIsRunning()
    {
        Populate(DefaultBinaryCache);

        var provider = new VcpkgCacheProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("vcpkg"));

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
