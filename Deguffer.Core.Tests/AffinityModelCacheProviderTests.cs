using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Affinity keeps the user's whole asset library in the folder that holds the downloaded models, so
/// the rule worth proving hardest is §5.2's: only <c>modelcache</c> is ever a target, and the
/// brushes, licences and receipts beside it are asserted to survive a run that empties it.
///
/// <para>The second rule is §5.2 one level up. A version folder is recognised only when its whole
/// name is a major version, because that is what decides which folders Deguffer looks inside at
/// all.</para>
/// </summary>
public sealed class AffinityModelCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public AffinityModelCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private AffinityModelCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Affinity 2's root, which is a hidden folder in the profile.</summary>
    private string ProfileRoot =>
        Path.Combine(_environment.UserProfile, AffinityProfiles.ProfileFolderName);

    /// <summary>Affinity 1 and 3's root, which is under the roaming application data.</summary>
    private string RoamingRoot =>
        Path.Combine(_environment.RoamingAppData, AffinityProfiles.RoamingFolderName);

    private static string Common(string root) => Path.Combine(root, AffinityProfiles.CommonName);

    private static string Version(string root, string version) => Path.Combine(Common(root), version);

    private static string ModelCache(string root, string version) =>
        Path.Combine(Version(root, version), AffinityModelCacheProvider.ModelCacheName);

    /// <summary>Create the shared version folder, with models in it unless told otherwise.</summary>
    private static string CreateVersion(string root, string version, bool withModels = true)
    {
        var directory = Version(root, version);
        Directory.CreateDirectory(directory);

        if (withModels)
        {
            var cache = ModelCache(root, version);
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache, "SegmentationEncoder_2.6.onnx"), new byte[65536]);
            File.WriteAllBytes(Path.Combine(cache, "SegmentationDecoder_2.6.onnx"), new byte[32768]);
        }

        return directory;
    }

    /// <summary>
    /// What Affinity actually keeps beside the models, which is the whole reason this provider
    /// enumerates rather than removing the version folder: an asset library, the brushes, the
    /// activation records and the live state three products share.
    /// </summary>
    private static IReadOnlyList<string> CreateWhatSitsBesideTheModels(string root, string version)
    {
        var directory = Version(root, version);

        string[] folders = ["user", "Licences", "Receipts", "Settings", "Plugins", "locks"];
        string[] files = ["ipc.dat", "cs.dat", "cs.json", "sp.db"];

        var made = new List<string>();

        foreach (var folder in folders)
        {
            var path = Path.Combine(directory, folder);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "content.dat"), new byte[1024]);
            made.Add(path);
        }

        // The asset library is the largest single thing in here, and the closest in shape to a cache.
        File.WriteAllBytes(Path.Combine(directory, "user", "assets.propcol"), new byte[262144]);
        File.WriteAllBytes(Path.Combine(directory, "user", "raster_brushes.propcol"), new byte[65536]);

        foreach (var file in files)
        {
            var path = Path.Combine(directory, file);
            File.WriteAllBytes(path, new byte[512]);
            made.Add(path);
        }

        return made;
    }

    [Fact]
    public async Task ReportsNotPresentWhenAffinityHasNeverRunOnThisMachine()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// Both roots are live on a machine that has run more than one major version, and an uninstalled
    /// version leaves its models behind — so neither root is evidence about the other.
    /// </summary>
    [Fact]
    public async Task TargetsTheModelsUnderBothOfTheRootsAffinityHasUsed()
    {
        CreateVersion(ProfileRoot, "2.0");
        CreateVersion(RoamingRoot, "3.0");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains(ModelCache(ProfileRoot, "2.0"), plan.TargetedPaths);
        Assert.Contains(ModelCache(RoamingRoot, "3.0"), plan.TargetedPaths);
    }

    /// <summary>
    /// The folder exists as soon as Affinity has been started once, before any feature has needed a
    /// model. That is present with nothing to do, not absent.
    /// </summary>
    [Fact]
    public async Task IsPresentWithNothingToDoWhenNoFeatureHasNeededAModelYet()
    {
        CreateVersion(RoamingRoot, "3.0", withModels: false);
        CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(
            plan.Notes,
            n => n.Message.Contains("has not downloaded any machine-learning models", StringComparison.Ordinal));
    }

    /// <summary>
    /// The §5.2 illustration this provider exists for. The model cache and the user's asset library
    /// are siblings, one folder apart from the activation records, and a rule that took the version
    /// folder would take all of it.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheRootTheSharedFolderOrAVersionFolder()
    {
        CreateVersion(RoamingRoot, "3.0");
        CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(RoamingRoot, plan.TargetedPaths);
        Assert.DoesNotContain(Common(RoamingRoot), plan.TargetedPaths);
        Assert.DoesNotContain(Version(RoamingRoot, "3.0"), plan.TargetedPaths);

        Assert.Contains(plan.ProtectedPaths, p => p.Path == RoamingRoot && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Common(RoamingRoot) && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Version(RoamingRoot, "3.0") && p.ExistedBefore);
    }

    /// <summary>
    /// Every entry standing beside the models is named on the plan, not merely the ones this provider
    /// happens to have heard of. §5.6 can only assert what it was told about, and what Serif adds to
    /// this folder next is exactly what nobody wrote a list entry for.
    /// </summary>
    [Fact]
    public async Task NamesEveryEntryBesideTheModelsSo56CanAssertItSurvived()
    {
        CreateVersion(RoamingRoot, "3.0");
        var beside = CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var unheardOf = Path.Combine(Version(RoamingRoot, "3.0"), "something-new");
        Directory.CreateDirectory(unheardOf);

        var plan = await CreateProvider().PlanAsync();

        Assert.All(
            [.. beside, unheardOf],
            path => Assert.Contains(plan.ProtectedPaths, p => p.Path == path && p.ExistedBefore));
    }

    /// <summary>
    /// §5.2's dangerous direction, one level above the cache. The rule decides which folders Deguffer
    /// looks inside for a model cache, so a child of the shared folder that is not a major version
    /// must stay in Tier 4 however much it looks like one.
    /// </summary>
    [Theory]
    [InlineData("3")]            // a single number is not how Affinity names a version folder.
    [InlineData("3.0.1")]        // three parts: more than Affinity has ever written.
    [InlineData("v3")]           // prefixed.
    [InlineData("3.x")]          // not numeric throughout.
    [InlineData("X3.0")]         // prefixed: must not match unanchored.
    [InlineData("3.0-backup")]   // something a person made.
    [InlineData("user")]         // an unrelated folder.
    public async Task LeavesAnUnrecognisedChildOfTheSharedFolderAloneAndSaysSo(string name)
    {
        CreateVersion(RoamingRoot, "3.0");

        var stranger = Path.Combine(Common(RoamingRoot), name);
        Directory.CreateDirectory(Path.Combine(stranger, AffinityModelCacheProvider.ModelCacheName));
        File.WriteAllBytes(
            Path.Combine(stranger, AffinityModelCacheProvider.ModelCacheName, "model.onnx"),
            new byte[65536]);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(plan.TargetedPaths, p => LongPath.Contains(stranger, p));
        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{name}' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == stranger && p.ExistedBefore);
    }

    /// <summary>
    /// The one child a version folder recognises. A second name arriving in this list is a decision
    /// about somebody's asset library, so it fails here rather than passing quietly.
    /// </summary>
    [Fact]
    public void TheOnlyDisposableChildOfAVersionFolderIsTheModelCache()
    {
        var disposable = new DisposableChildSet(AffinityModelCacheProvider.SharedChildren);

        Assert.Equal(
            [AffinityModelCacheProvider.ModelCacheName],
            disposable.DisposableNames.Order(StringComparer.Ordinal));

        Assert.All(
            AffinityModelCacheProvider.SharedChildren.Where(c => c.Name != AffinityModelCacheProvider.ModelCacheName),
            c => Assert.Equal(SafetyTier.DoNotTouch, c.Tier));
    }

    /// <summary>
    /// Tier 2, not Tier 1: nothing fetches a model back on its own, so the next selection is what
    /// discovers it has gone and waits for a download. That must never be pre-selected.
    /// </summary>
    [Fact]
    public void IsTierTwoSoItIsOfferedButNeverPreSelected()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.True(provider.Tier.IsOfferable());
        Assert.False(provider.Tier.IsPreSelectedByDefault());
    }

    [Fact]
    public async Task NamesTheVersionEachCacheBelongsToInAColumn()
    {
        CreateVersion(ProfileRoot, "2.0");
        CreateVersion(RoamingRoot, "3.0");

        var steps = (await CreateProvider().PlanAsync()).Steps
            .OfType<DeleteStep>()
            .ToDictionary(step => step.Path, StringComparer.OrdinalIgnoreCase);

        Assert.Equal([new ItemFacet("Version", "2.0")], steps[ModelCache(ProfileRoot, "2.0")].Facets);
        Assert.Equal([new ItemFacet("Version", "3.0")], steps[ModelCache(RoamingRoot, "3.0")].Facets);
    }

    /// <summary>
    /// §7's age, which is what separates the version in daily use from the one an uninstall left
    /// behind eleven months ago.
    /// </summary>
    [Fact]
    public async Task CarriesTheAgeOfEachModelCache()
    {
        CreateVersion(RoamingRoot, "3.0");

        var step = Assert.Single((await CreateProvider().PlanAsync()).Steps);

        Assert.NotNull(step.LastWritten);
        Assert.NotEqual("Unknown", RelativeAge.Describe(step.LastWritten, DateTime.UtcNow));
    }

    /// <summary>
    /// A cache is kept by what it is, never by where it was found, so moving the profile must not
    /// release a decision the user already made about a version's models.
    /// </summary>
    [Fact]
    public async Task IdentifiesACacheByItsVersionRatherThanByItsPath()
    {
        CreateVersion(RoamingRoot, "3.0");

        var moved = new FakeUserEnvironment(_temp.CreateDirectory("elsewhere"));
        CreateVersion(Path.Combine(moved.RoamingAppData, AffinityProfiles.RoamingFolderName), "3.0");

        var here = Assert.Single((await CreateProvider().PlanAsync()).Steps.OfType<DeleteStep>());
        var there = Assert.Single(
            (await new AffinityModelCacheProvider(moved, new FakeProcessRunner(), FakeProcessInspector.NothingRunning)
                .PlanAsync()).Steps.OfType<DeleteStep>());

        Assert.NotEqual(here.Path, there.Path);
        Assert.Equal(here.Identity, there.Identity);
    }

    /// <summary>
    /// The same version number under both roots is two caches, not one, so the keep list must be able
    /// to hold a decision about each.
    /// </summary>
    [Fact]
    public async Task TellsTheSameVersionNumberUnderTheTwoRootsApart()
    {
        CreateVersion(ProfileRoot, "2.0");
        CreateVersion(RoamingRoot, "2.0");

        var keys = (await CreateProvider().PlanAsync()).Steps
            .OfType<DeleteStep>()
            .Select(step => step.Identity!.Key)
            .ToList();

        Assert.Equal(2, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// §5.6 driven all the way through, which is the half a plan-only assertion cannot reach. The
    /// asset library and the models are siblings in one folder, so this is exactly where an
    /// over-broad rule takes one with the other, and a plan naming a survivor is not evidence that it
    /// lived.
    /// </summary>
    [Fact]
    public async Task ExecutingTakesTheModelsAndLeavesTheAssetLibraryStanding()
    {
        CreateVersion(ProfileRoot, "2.0");
        CreateVersion(RoamingRoot, "3.0");

        var beside = new List<string>();
        beside.AddRange(CreateWhatSitsBesideTheModels(ProfileRoot, "2.0"));
        beside.AddRange(CreateWhatSitsBesideTheModels(RoamingRoot, "3.0"));

        string[] mustSurvive =
        [
            ProfileRoot,
            RoamingRoot,
            Common(ProfileRoot),
            Common(RoamingRoot),
            Version(ProfileRoot, "2.0"),
            Version(RoamingRoot, "3.0"),
            .. beside,
        ];

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(ModelCache(ProfileRoot, "2.0")));
        Assert.False(Directory.Exists(ModelCache(RoamingRoot, "3.0")));

        Assert.All(
            mustSurvive,
            path => Assert.True(
                Directory.Exists(path) || File.Exists(path),
                $"{path} was removed"));

        Assert.True(
            File.Exists(Path.Combine(Version(RoamingRoot, "3.0"), "user", "assets.propcol")),
            "The asset library was removed.");

        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Moving a profile folder onto another drive with a junction is ordinary, and the enumeration
    /// never classifies the directory it is handed: it would hand back the far side's children and
    /// pass every §5.6 assertion named for this root, because each survivor resolves through the same
    /// link.
    /// </summary>
    [Fact]
    public async Task DeclinesAProfileRootThatIsItselfALink()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var stranger = Path.Combine(
            outside, AffinityProfiles.CommonName, "3.0", AffinityModelCacheProvider.ModelCacheName);
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "model.onnx"), new byte[65536]);

        Directory.CreateSymbolicLink(RoamingRoot, outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        // Not HasUnreadableRoot: Windows refused nothing here. Deguffer declined, and the two states
        // send the reader to different places.
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>The same decline one level in, where only the shared folder was moved.</summary>
    [Fact]
    public async Task DeclinesTheSharedFolderWhenItIsALink()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var stranger = Path.Combine(outside, "3.0", AffinityModelCacheProvider.ModelCacheName);
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "model.onnx"), new byte[65536]);

        Directory.CreateDirectory(RoamingRoot);
        Directory.CreateSymbolicLink(Common(RoamingRoot), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// A model cache relocated by junction. Deleting through it would reach a tree nobody classified,
    /// and the folder it left behind measures nothing while saying nothing about what is on the far
    /// side.
    /// </summary>
    [Fact]
    public async Task DeclinesAModelCacheThatIsALink()
    {
        CreateVersion(RoamingRoot, "3.0", withModels: false);
        CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var outside = _temp.CreateDirectory("elsewhere");
        File.WriteAllBytes(Path.Combine(outside, "model.onnx"), new byte[65536]);

        Directory.CreateSymbolicLink(ModelCache(RoamingRoot, "3.0"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(File.Exists(Path.Combine(outside, "model.onnx")));
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));
    }

    /// <summary>
    /// The folder is found by name, and a listing right is separate from a traverse right — so a
    /// refusal here yields a plan with no steps, which the shell would otherwise render as "already
    /// clear" about a folder nobody read.
    /// </summary>
    [Fact]
    public async Task ASharedFolderThatWillNotBeListedIsSaidSoRatherThanLeftLookingAlreadyClear()
    {
        CreateVersion(RoamingRoot, "3.0");

        using var denied = new DeniedDirectory(Common(RoamingRoot));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(
            plan.Notes,
            n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(Common(RoamingRoot)));
        Assert.Empty(plan.TargetedPaths);

        // The warning and this sentence in one plan would have the plan deny what the same pass
        // found. It is the sentence for an empty machine, and this machine was never read.
        Assert.DoesNotContain(
            plan.Notes,
            n => n.Message.Contains("has not downloaded any machine-learning models", StringComparison.Ordinal));
    }

    /// <summary>
    /// A version folder relocated by junction. It must never reach the version list: looking inside it
    /// would classify a tree Deguffer never saw, and every §5.6 assertion named for this root would
    /// then resolve through the same link and pass.
    /// </summary>
    [Fact]
    public async Task DeclinesAVersionFolderThatIsALink()
    {
        Directory.CreateDirectory(Common(RoamingRoot));

        var outside = _temp.CreateDirectory("elsewhere");
        var stranger = Path.Combine(outside, AffinityModelCacheProvider.ModelCacheName);
        Directory.CreateDirectory(stranger);
        File.WriteAllBytes(Path.Combine(stranger, "model.onnx"), new byte[65536]);

        Directory.CreateSymbolicLink(Version(RoamingRoot, "3.0"), outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Leaving '3.0' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path == Version(RoamingRoot, "3.0"));
    }

    /// <summary>
    /// Affinity writes a folder called <c>modelcache</c>. A file of that name is not the cache, and
    /// the reason the child table gives for the name describes a download Affinity fetches again —
    /// which would be said about a path the plan is leaving alone.
    /// </summary>
    [Fact]
    public async Task AFileWearingTheCachesNameIsLeftAloneWithItsOwnReason()
    {
        CreateVersion(RoamingRoot, "3.0", withModels: false);
        CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var impostor = ModelCache(RoamingRoot, "3.0");
        File.WriteAllBytes(impostor, new byte[4096]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);

        var protectedPath = Assert.Single(plan.ProtectedPaths, p => p.Path == impostor);

        Assert.True(protectedPath.ExistedBefore);
        Assert.DoesNotContain("downloads them again", protectedPath.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case §5.6 cannot survive. A full path through a folder the account may not list still
    /// resolves, so probing for <c>modelcache</c> by name would find it and offer it — while the
    /// asset library beside it was never seen, and so could never be asserted to have survived.
    /// Deguffer leaves such a folder alone and says so.
    /// </summary>
    [Fact]
    public async Task LeavesAVersionFolderAloneWhenItsOwnContentsWillNotBeListed()
    {
        CreateVersion(RoamingRoot, "3.0");
        CreateWhatSitsBesideTheModels(RoamingRoot, "3.0");

        var version = Version(RoamingRoot, "3.0");

        using var denied = new DeniedDirectory(version);

        // The cache is still reachable by its full name, which is what makes the refusal dangerous
        // rather than merely inconvenient.
        Assert.True(Directory.Exists(ModelCache(RoamingRoot, "3.0")));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.HasUnreadableRoot);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(version));
    }

    /// <summary>
    /// A model file sits directly in the cache, but Affinity is free to nest one and a measurement
    /// that stopped at the top level would report almost nothing.
    ///
    /// Not a §6.3 assertion, and not merely a weak one on a machine with <c>LongPathsEnabled</c> set:
    /// .NET applies <c>\\?\</c> itself past 260 characters, so the measurement succeeds however Core
    /// handles the path. <see cref="LongPathTests.TheRuntimeStillReachesPastMaxPathWithoutOurPrefix"/>
    /// is the one test that would notice if that stopped being true. What this does prove is that the
    /// size reaches content nested inside the cache rather than stopping at its first level.
    /// </summary>
    [Fact]
    public async Task MeasuresModelsNestedDeeplyInsideTheCache()
    {
        CreateVersion(RoamingRoot, "3.0");

        // The models written at the top level are 98,304 bytes, so a figure above them is one that
        // came from further down rather than from the first listing.
        const int Shallow = 65536 + 32768;

        var deep = ModelCache(RoamingRoot, "3.0");
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('w', 40));
        }

        Assert.True(deep.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "model.onnx")), new byte[65536]);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(ModelCache(RoamingRoot, "3.0"), plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > Shallow, "Models nested inside the cache were not measured.");
    }
}
