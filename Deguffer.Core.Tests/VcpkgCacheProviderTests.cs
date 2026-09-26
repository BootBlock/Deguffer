using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

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
    /// A binary cache as vcpkg lays one out, for a folder a variable moves it to: one package, filed in
    /// the shard named by the first two digits of its hash.
    /// </summary>
    private static string PopulateBinaryCache(string directory, int bytes = 4096)
    {
        const string abi = "3f9c1e2d4b5a69788796a5b4c3d2e1f00112233445566778899aabbccddeeff0";
        var shard = Path.Combine(directory, abi[..2]);
        Directory.CreateDirectory(shard);
        File.WriteAllBytes(Path.Combine(shard, abi + ".zip"), new byte[bytes]);
        return directory;
    }

    /// <summary>
    /// A downloads folder as vcpkg leaves one, for a folder a variable moves it to: a source archive
    /// and a tool vcpkg unpacked under <c>tools</c>.
    /// </summary>
    private static string PopulateDownloads(string directory, int bytes = 4096)
    {
        Populate(Path.Combine(directory, "tools", "cmake-3.30.1-windows"));
        File.WriteAllBytes(Path.Combine(directory, "zlib-1.3.1.tar.gz"), new byte[bytes]);
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

    /// <summary>
    /// A clone to deny the attribute read of, built without the marker. The one refusal a test may
    /// build is an access rule on a directory, and it leaves a file inside readable: NTFS answers for
    /// the file out of the folder's own index. A link Windows will not follow refuses the marker as
    /// well, and a refused marker probe reads as no marker, which is this.
    /// </summary>
    private static string CreateRefusableClone(string root)
    {
        Populate(Path.Combine(root, "buildtrees"));
        Populate(Path.Combine(root, "installed"));

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
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(root, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
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
            && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// A configured value says where a cache is. It is never a licence to remove the directory that
    /// holds the tool — which is what naming the clone itself would do, taking 'installed' with it
    /// while the same plan asserted that survives.
    /// </summary>
    [Theory]
    [InlineData(VcpkgDiscovery.BinaryCacheVariable)]
    [InlineData(VcpkgDiscovery.DownloadsVariable)]
    public async Task RefusesACacheVariableThatNamesTheCloneItself(string variable)
    {
        var root = CreateClone();
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(variable, root);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(root, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, targeted => Assert.False(
            root.Equals(targeted, StringComparison.OrdinalIgnoreCase),
            $"{targeted} is the clone itself."));

        var result = await CreateProvider().ExecuteAsync(
            plan with { Tier = SafetyTier.RegenerableCache });

        Assert.True(Directory.Exists(Path.Combine(root, "installed")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// One level in from the guard above, and worse in a quieter way: the same plan would target the
    /// directory and assert it survived, which is the contradiction §5.6 exists to catch rather than
    /// to produce.
    /// </summary>
    [Theory]
    [InlineData("installed")]
    [InlineData("ports")]
    public async Task RefusesACacheVariableThatNamesSomethingItPromisesToLeaveAlone(string name)
    {
        var root = CreateClone();
        var kept = Path.Combine(root, name);
        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, kept);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(kept, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(kept, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same refusal for the clone Deguffer declined to look inside. Without the marker there is
    /// no declaration naming 'installed' a survivor either, so §5.6 would have passed vacuously
    /// while it went — which is the worst version of this failure rather than a lesser one.
    /// </summary>
    [Fact]
    public async Task RefusesACacheVariableThatNamesACloneItWouldNotLookInside()
    {
        var root = Path.Combine(_temp.Path, "dev", "vcpkg");
        var installed = Populate(Path.Combine(root, "installed"));
        Populate(Path.Combine(root, "buildtrees"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, root);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(installed));
    }

    /// <summary>
    /// A refusal the user can see the folder for has to be said out loud. "vcpkg has cached nothing"
    /// contradicts the disk, and "set VCPKG_ROOT" is advice somebody who set it has already taken.
    /// </summary>
    [Fact]
    public async Task SaysWhichDirectoryItDeclinedForWantOfTheMarker()
    {
        var root = Path.Combine(_temp.Path, "dev", "vcpkg");
        Populate(Path.Combine(root, "buildtrees"));
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(root, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains(VcpkgDiscovery.RootMarker, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n =>
            n.Message.Contains("cached nothing", StringComparison.Ordinal));
    }

    /// <summary>
    /// A clone Windows will not describe is not a clone that is missing, and not one without the
    /// marker either. The two-state probe read it as nothing at all: the row was not drawn, and had
    /// it been, the plan would have told somebody who set <c>VCPKG_ROOT</c> to set it.
    /// </summary>
    [Fact]
    public async Task ACloneWindowsWillNotDescribeIsSaidToBeUnreachedRatherThanMissing()
    {
        var root = CreateRefusableClone(Path.Combine(_temp.Path, "dev", "vcpkg"));
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        using var denied = DeniedDirectory.WithUnreadableAttributes(root);

        var provider = CreateProvider();
        Assert.Equal(root, provider.Locate().UnreachedRoot);
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(root, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("cached nothing", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(VcpkgDiscovery.RootVariable, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains(VcpkgDiscovery.RootMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// A cache variable naming a clone Windows will not describe is refused as vcpkg's own directory,
    /// as it is for a clone without the marker: a refusal is no more evidence that it is not a clone.
    /// </summary>
    [Fact]
    public async Task RefusesACacheVariableThatNamesACloneWindowsWillNotDescribe()
    {
        var root = CreateRefusableClone(Path.Combine(_temp.Path, "dev", "vcpkg"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, root);

        using var denied = DeniedDirectory.WithUnreadableAttributes(root);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.Contains("that is vcpkg's own directory", StringComparison.Ordinal));
    }

    /// <summary>
    /// The search for the binary cache stops at a local one Windows will not describe, because
    /// vcpkg's may have stopped there too. Passing it for the roaming one sized a cache vcpkg may not
    /// be using, and said nothing about the one it may be.
    /// </summary>
    [Fact]
    public async Task TheBinaryCacheSearchStopsAtALocalCacheWindowsWillNotDescribe()
    {
        Populate(DefaultBinaryCache);
        var roaming = Populate(Path.Combine(_environment.RoamingAppData, "vcpkg", "archives"));

        using var denied = DeniedDirectory.WithUnreadableAttributes(DefaultBinaryCache);

        var provider = CreateProvider();
        Assert.Equal(DefaultBinaryCache, provider.Locate().BinaryCache);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(roaming, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning
            && n.Message.Contains(DefaultBinaryCache, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Presence and planning ask the same question of the same disk, so the answer is found once
    /// (G4). Reading the environment is the observable part of doing so.
    /// </summary>
    [Fact]
    public async Task LocatesOnceAcrossAPlanningPass()
    {
        Populate(DefaultBinaryCache);
        var provider = CreateProvider();

        await provider.IsPresentAsync();
        var afterFirst = _environment.EnvironmentReads;
        await provider.PlanAsync();

        Assert.True(afterFirst > 0, "discovery never read the environment at all");
        Assert.Equal(afterFirst, _environment.EnvironmentReads);
    }

    /// <summary>
    /// A variable naming one of the account's own folders as a cache. The folder is removed whole,
    /// so this would take all of Downloads or the Desktop, and §5.6 protected only the profile above
    /// it. The folder holds exactly what vcpkg writes, so nothing but the account-folder refusal can
    /// be what keeps it.
    /// </summary>
    [Theory]
    [InlineData(VcpkgDiscovery.BinaryCacheVariable, "Documents")]
    [InlineData(VcpkgDiscovery.BinaryCacheVariable, "Desktop")]
    [InlineData(VcpkgDiscovery.DownloadsVariable, "Downloads")]
    public async Task NeverTargetsOneOfTheAccountsOwnFoldersAVariableNamesAsACache(string variable, string name)
    {
        var root = CreateClone();
        var folder = Path.Combine(_environment.UserProfile, name);

        if (variable == VcpkgDiscovery.BinaryCacheVariable)
        {
            PopulateBinaryCache(folder);
        }
        else
        {
            PopulateDownloads(folder);
        }

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(variable, folder);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.NotEmpty(plan.TargetedPaths);
        Assert.All(plan.TargetedPaths, targeted => Assert.False(IsAtOrUnder(targeted, folder), $"{targeted} is in {name}."));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(folder, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(folder, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("one of your own folders", StringComparison.Ordinal));

        var result = await provider.ExecuteAsync(plan with { Tier = SafetyTier.RegenerableCache });

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(folder));
        Assert.True(Directory.Exists(Path.Combine(root, "installed")));
    }

    /// <summary>
    /// A folder inside one of the account's own is somewhere somebody chose to keep a cache, and one
    /// holding what vcpkg writes is vcpkg's.
    /// </summary>
    [Fact]
    public async Task TakesACacheKeptInsideOneOfTheAccountsOwnFolders()
    {
        var moved = PopulateBinaryCache(Path.Combine(_environment.UserProfile, "Downloads", "vcpkg-archives"));
        _environment.WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([moved], plan.TargetedPaths);
    }

    /// <summary>
    /// A folder a variable names is vcpkg's binary cache only if it holds nothing vcpkg did not put
    /// there, and at least one package vcpkg did. Anything else means somebody else keeps things in
    /// it, and the folder is removed whole.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", "'notes.txt' is in it")]
    [InlineData("projects", "'projects' is in it")]
    [InlineData(null, "nothing in it is a package vcpkg cached")]
    public async Task LeavesAMovedBinaryCacheAloneUnlessItHoldsOnlyWhatVcpkgWrites(string? stranger, string reason)
    {
        var moved = Path.Combine(_temp.Path, "shared", "vcpkg-archives");

        if (stranger is null)
        {
            Populate(Path.Combine(moved, "ab"));
        }
        else
        {
            PopulateBinaryCache(moved);

            if (Path.HasExtension(stranger))
            {
                File.WriteAllText(Path.Combine(moved, stranger), "mine");
            }
            else
            {
                Populate(Path.Combine(moved, stranger));
            }
        }

        _environment.WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains(reason, StringComparison.Ordinal));
    }

    /// <summary>
    /// vcpkg writes no marker in its downloads folder, and the archives are named by each port. The
    /// tools it unpacks under <c>tools</c> are the one thing that says the folder is vcpkg's, so a
    /// folder without them is left alone — and the clone's own downloads folder is not offered in its
    /// place, because the variable still says vcpkg looks elsewhere.
    /// </summary>
    [Fact]
    public async Task LeavesAMovedDownloadsFolderAloneWithoutTheToolsVcpkgUnpacks()
    {
        var root = CreateClone();
        var moved = Populate(Path.Combine(_temp.Path, "shared", "vcpkg-downloads"));
        Populate(Path.Combine(moved, "tools", "my-scripts"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.DownloadsVariable, moved);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(moved, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(root, "downloads"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains(moved, StringComparison.OrdinalIgnoreCase)
            && n.Message.Contains("'tools' folder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HonoursTheConfiguredBinaryCacheLocation()
    {
        var moved = PopulateBinaryCache(Path.Combine(_temp.Path, "shared", "vcpkg-archives"));
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
        var moved = PopulateDownloads(Path.Combine(_temp.Path, "shared", "vcpkg-downloads"));
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
        SymbolicLink.ToDirectory(Path.Combine(root, "buildtrees"), outside);
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(Path.Combine(root, "buildtrees"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
        Assert.True(Directory.Exists(outside));

        // The boundary on CleanupPlan.WasNotExamined: downloads and packages are still targeted, so
        // the row has a size and reads "Ready to clean".
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// The same decline over every declared directory at once, which is what a clone relocated onto
    /// a scratch drive by junction looks like. The probe resolves through the links, so the row is
    /// present, measures zero, and its figure covers none of the subject.
    /// </summary>
    [Fact]
    public async Task ACloneWhoseEveryDeclaredDirectoryIsALinkIsNotCalledClear()
    {
        var root = CreateClone();

        var outside = Populate(Path.Combine(_temp.Path, "elsewhere"));

        foreach (var name in new[] { "buildtrees", "downloads", "packages" })
        {
            Directory.Delete(Path.Combine(root, name), recursive: true);
            SymbolicLink.ToDirectory(Path.Combine(root, name), outside);
        }

        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// A vcpkg buildtree carries a port's whole source tree under a triplet directory, which is where
    /// <c>MAX_PATH</c> is met in practice, so scratch content that deep is measured and then
    /// reclaimed in full.
    ///
    /// Reach and removal are the whole of it. .NET prepends <c>\\?\</c> to a path of 260 characters
    /// or more before it calls Win32, so the run would look the same with Core's prefixing removed;
    /// <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/> keeps that
    /// honest, and §6.3 is proved where a path's form is asserted rather than its outcome.
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
        Assert.Contains(verification.Failures, c => c.Subject.Equals(installed, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// §7.1 over the clone <c>VCPKG_ROOT</c> names and the folder holding a binary cache
    /// <c>VCPKG_DEFAULT_BINARY_CACHE</c> moved. <c>installed</c> is what every project on the
    /// machine links against and usually the largest thing in a clone, and the plan never touches it.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesTheCloneAndTheFolderHoldingAMovedBinaryCache()
    {
        var root = CreateClone(Path.Combine(_environment.UserProfile, "dev", "vcpkg"));
        var holder = Path.Combine(_environment.UserProfile, "caches");
        var binaryCache = PopulateBinaryCache(Path.Combine(holder, "vcpkg-archives"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, binaryCache);

        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.False(policy.MayRemove(root).IsAllowed);
        Assert.All(
            new[] { "installed", "ports", "triplets", "versions", "scripts", VcpkgDiscovery.RootMarker },
            child => Assert.False(policy.MayRemove(Path.Combine(root, child)).IsAllowed, child));
        Assert.All(
            new[] { "buildtrees", "downloads", "packages" },
            child => Assert.True(policy.MayRemove(Path.Combine(root, child)).IsAllowed, child));

        Assert.False(policy.MayRemove(holder).IsAllowed);
        Assert.True(policy.MayRemove(binaryCache).IsAllowed);

        // The plan protects the folder holding the cache and nothing else in it.
        Assert.True(policy.MayRemove(Populate(Path.Combine(holder, "unrelated"))).IsAllowed);
    }

    /// <summary>
    /// The clone's <c>downloads</c> leaves what Explore offers exactly when it leaves the plan: once
    /// <c>VCPKG_DOWNLOADS</c> has moved the downloads, the folder of that name in the clone is no
    /// longer vcpkg's to refill, and the directory holding the moved one must survive.
    /// </summary>
    [Fact]
    public async Task ExploreStopsOfferingTheClonesDownloadsOnceAVariableHasMovedThem()
    {
        var root = CreateClone(Path.Combine(_environment.UserProfile, "dev", "vcpkg"));
        var holder = Path.Combine(_environment.UserProfile, "downloads-elsewhere");
        var downloads = PopulateDownloads(Path.Combine(holder, "vcpkg-downloads"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.DownloadsVariable, downloads);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [provider]);

        Assert.DoesNotContain(Path.Combine(root, "downloads"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.False(policy.MayRemove(Path.Combine(root, "downloads")).IsAllowed);

        Assert.False(policy.MayRemove(holder).IsAllowed);
        Assert.True(policy.MayRemove(downloads).IsAllowed);
        Assert.True(policy.MayRemove(Populate(Path.Combine(holder, "unrelated"))).IsAllowed);
    }

    /// <summary>
    /// §7.1 over a clone Windows will not describe. Nothing established what is in it, so Explore
    /// refuses all of it, the scratch directories included, rather than being told nothing.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAllOfACloneWindowsWillNotDescribe()
    {
        var root = CreateRefusableClone(Path.Combine(_environment.UserProfile, "dev", "vcpkg"));
        _environment.WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root);

        using var denied = DeniedDirectory.WithUnreadableAttributes(root);

        var roots = await CreateProvider().DiscoverToolRootsAsync();

        var declared = Assert.Single(roots, r => r.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
        Assert.All(
            new[] { "installed", "buildtrees", "downloads", "packages" },
            child => Assert.False(declared.Recognises(child), child));

        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.False(policy.MayRemove(Path.Combine(root, "buildtrees")).IsAllowed);
    }

    /// <summary>
    /// A binary cache a variable puts directly inside the clone makes the clone its container as
    /// well. Explore must go on refusing what the clone protects: <c>installed</c> is what every
    /// project on the machine links against.
    /// </summary>
    [Fact]
    public async Task ACacheInsideTheCloneLeavesTheClonesProtectionsStanding()
    {
        var root = CreateClone(Path.Combine(_environment.UserProfile, "dev", "vcpkg"));
        var binaryCache = Populate(Path.Combine(root, "archives"));

        _environment
            .WithEnvironmentVariable(VcpkgDiscovery.RootVariable, root)
            .WithEnvironmentVariable(VcpkgDiscovery.BinaryCacheVariable, binaryCache);

        var policy = await ExploreActionPolicy.ForAsync(
            new FakeSystemDirectories(_temp.Path), _environment, new FakeVolumeInventory(), [CreateProvider()]);

        Assert.False(policy.MayRemove(Path.Combine(root, "installed")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(root, "ports")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(root, "buildtrees")).IsAllowed);
    }
}
