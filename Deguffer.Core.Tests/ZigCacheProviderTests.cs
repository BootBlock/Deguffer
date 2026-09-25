using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Zig's global cache holds the fetched packages beside what it compiled, and its build outputs are
/// named by manifests beside them, so most of these are about what must not go, or must not go alone.
///
/// <para>Everything runs against a synthetic profile through <see cref="FakeUserEnvironment"/>. No Zig
/// toolchain was installed on the machine these were written on, and a rule that could only be proved
/// where Zig is present would not be a rule.</para>
/// </summary>
public sealed class ZigCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public ZigCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private ZigCacheProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private string Root => Path.Combine(_environment.LocalAppData, "zig");

    private string Child(string name) => Path.Combine(Root, name);

    /// <summary>A directory holding one file, so it measures above zero and is selectable.</summary>
    private static string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>Every child Zig 0.15 writes, each holding a file.</summary>
    private void CreateFullCache()
    {
        foreach (var name in (string[])["h", "o", "z", "b", "tmp", "p"])
        {
            Populate(Child(name));
        }
    }

    private static DeleteDirectoryStep OutputsStep(CleanupPlan plan) =>
        Assert.Single(plan.Steps.OfType<DeleteDirectoryStep>(), s => s.IndexedBy.Count > 0);

    [Fact]
    public async Task ReportsNotPresentWhenZigHasNeverBuilt()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
    }

    /// <summary>A cache holding only fetched packages has nothing this row would offer.</summary>
    [Fact]
    public async Task ACacheHoldingOnlyPackagesIsNotPresence()
    {
        Populate(Child("p"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// The outputs and their index are one step, with the index first, and the three independent caches
    /// are one step each.
    /// </summary>
    [Fact]
    public async Task PlansTheOutputsWithTheirIndexAndEachOtherCacheOnItsOwn()
    {
        CreateFullCache();

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        var outputs = OutputsStep(plan);
        Assert.Equal(Child("o"), outputs.Path);
        Assert.Equal([Child("h"), Child("o")], outputs.Destroys);
        Assert.Equal(8192, outputs.EstimatedBytes);

        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal(
            ((string[])["b", "h", "o", "tmp", "z"]).Select(Child),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// §5.2's trap here: a package fetched from a web address comes back only while the address serves
    /// it, so the packages are declared and left alone rather than merely unrecognised.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheRootOrTheFetchedPackages()
    {
        CreateFullCache();

        var plan = await CreateProvider().PlanAsync();

        foreach (var kept in (string[])[Root, Child("p")])
        {
            Assert.DoesNotContain(kept, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(kept, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var packages = ZigCacheProvider.Children.Classify("p");
        Assert.Equal(SafetyTier.DoNotTouch, packages.Tier);
        Assert.Contains(plan.Notes, n => n.Message.Contains(packages.Reason, StringComparison.Ordinal));
    }

    /// <summary>§5.2's dangerous direction: a child nobody declared lands in Tier 4.</summary>
    [Fact]
    public async Task AnUndeclaredChildIsTier4AndLeftAlone()
    {
        CreateFullCache();
        var unknown = Populate(Child("c"));

        Assert.Equal(SafetyTier.DoNotTouch, ZigCacheProvider.Children.Classify("c").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith("Leaving 'c' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(unknown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryDeclaredDisposableChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        Assert.All(
            ZigCacheProvider.Children.DisposableNames,
            name => Assert.Equal(provider.Tier, ZigCacheProvider.Children.Classify(name).Tier));
    }

    /// <summary>
    /// Outputs with no index are left alone: a build between the preview and the clean would write an
    /// index the step knows nothing about, and then the clean would take the outputs it names.
    /// </summary>
    [Fact]
    public async Task OutputsWithNoIndexAreLeftAlone()
    {
        Populate(Child("o"));
        Populate(Child("z"));

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Child("z")], plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith("Leaving 'o' alone", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Child("o"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An index with no outputs beside it is an ordinary cache, and goes on its own.</summary>
    [Fact]
    public async Task AnIndexWithNoOutputsGoesOnItsOwn()
    {
        Populate(Child("h"));

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.IsType<DeleteDirectoryStep>(Assert.Single(plan.Steps));
        Assert.Equal(Child("h"), step.Path);
        Assert.Empty(step.IndexedBy);
    }

    /// <summary>
    /// Zig reads its manifests through a linked index, so the outputs they name stay with the link.
    /// </summary>
    [Fact]
    public async Task OutputsWhoseIndexIsALinkAreLeftAlone()
    {
        Populate(Child("o"));
        var elsewhere = Populate(Path.Combine(_temp.Path, "elsewhere", "h"));
        SymbolicLink.ToDirectory(Child("h"), elsewhere);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Message.StartsWith("Leaving 'h' alone: A link", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Message.StartsWith("Leaving 'o' alone", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = Populate(Path.Combine(outside, "tmp"));
        SymbolicLink.ToDirectory(Root, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    [Fact]
    public async Task HonoursTheCacheVariableWhenItNamesAFolderOfItsOwn()
    {
        var moved = Path.Combine(_temp.Path, "caches", "zig");
        Populate(Path.Combine(moved, "z"));
        _environment.WithEnvironmentVariable(ZigCacheProvider.CacheVariable, moved.Replace('\\', '/') + "/");

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal([Path.Combine(moved, "z")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.Ordinal));
    }

    /// <summary>
    /// Zig resolves a relative value against the build's working directory, which Deguffer is not, and
    /// the default location is not what the user asked for either.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenTheCacheVariableIsRelative()
    {
        Populate(Child("z"));
        _environment.WithEnvironmentVariable(ZigCacheProvider.CacheVariable, @"..\zig-cache");

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.ToolRoots);

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// The names Zig uses in its cache are too short to vouch for anything. A variable naming a folder
    /// that holds the profile's own folders, or a drive root, would offer whatever 'tmp' is there.
    /// </summary>
    [Fact]
    public async Task WillNotTreatAFolderHoldingTheProfileAsZigs()
    {
        var shared = Populate(Path.Combine(_environment.UserProfile, "tmp"));
        _environment.WithEnvironmentVariable(ZigCacheProvider.CacheVariable, _environment.UserProfile);

        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.Empty(provider.ToolRoots);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(shared, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("will not treat that as Zig's cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WillNotTreatADriveRootAsZigs()
    {
        _environment.WithEnvironmentVariable(ZigCacheProvider.CacheVariable, @"Q:\");

        var plan = await CreateProvider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("root of a drive", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6, executed. The run removes what Zig compiled, and the root, the packages and anything
    /// unrecognised are all still there afterwards.
    /// </summary>
    [Fact]
    public async Task ExecutingRemovesWhatZigCompiledAndLeavesEverythingElseStanding()
    {
        CreateFullCache();
        var unknown = Populate(Child("c"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.Equal(5 * 4096, result.BytesReclaimed);

        foreach (var name in (string[])["h", "o", "z", "b", "tmp"])
        {
            Assert.False(Directory.Exists(Child(name)), name);
        }

        Assert.True(Directory.Exists(Root));
        Assert.True(File.Exists(Path.Combine(Child("p"), "payload.bin")));
        Assert.True(Directory.Exists(unknown));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfThePackagesVanished()
    {
        CreateFullCache();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(Child("p"), recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(Child("p"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The whole reason the outputs and the index are one step: an index file Windows will not let go
    /// of still names outputs, so the outputs stay, every one of them.
    /// </summary>
    [Fact]
    public async Task AnIndexThatWillNotAllGoKeepsTheOutputsStanding()
    {
        CreateFullCache();
        var manifest = Path.Combine(Child("h"), "payload.bin");
        var output = Path.Combine(Child("o"), "payload.bin");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        CleanupResult result;
        using (new UndeletableFile(manifest))
        {
            result = await provider.ExecuteAsync(plan);
        }

        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(output));
        Assert.False(Directory.Exists(Child("z")));

        var step = Assert.Single(result.Steps, s => s.Description.Contains(Child("o"), StringComparison.Ordinal));
        Assert.Contains("part of its index could not be removed", step.Message, StringComparison.Ordinal);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// An index already gone when the clean arrives names nothing, so the outputs go. The rule is that
    /// nothing of the index is left, not that the removal took it.
    /// </summary>
    [Fact]
    public async Task AnIndexGoneBeforeTheCleanLetsTheOutputsGo()
    {
        CreateFullCache();

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(Child("h"), recursive: true);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(Child("o")));
        Assert.True(Directory.Exists(Child("p")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Once a clean found part of the index held, a preview that still finds it held does not offer the
    /// outputs, and says why: the clean would leave them again.
    /// </summary>
    [Fact]
    public async Task APreviewWhileTheIndexIsStillHeldDoesNotOfferTheOutputs()
    {
        CreateFullCache();
        var manifest = Path.Combine(Child("h"), "payload.bin");
        var provider = CreateProvider();

        using (new UndeletableFile(manifest))
        {
            await provider.ExecuteAsync(await provider.PlanAsync());

            Populate(Child("z"));

            var again = await provider.PlanAsync();

            Assert.Equal([Child("z")], again.TargetedPaths);
            Assert.Contains(again.Notes, n => n.Severity == PlanNoteSeverity.Warning
                && n.Message.Contains("part of its index", StringComparison.Ordinal));
            Assert.Contains(again.ProtectedPaths, p => p.Path.Equals(Child("o"), StringComparison.OrdinalIgnoreCase));
        }

        var released = await provider.PlanAsync();

        Assert.Contains(Child("o"), released.TargetedPaths);
    }

    /// <summary>
    /// Under the guard, a recent manifest could name an old output, so the pair cannot go part-way and
    /// is withdrawn whole. The independent caches are still offered.
    /// </summary>
    [Fact]
    public async Task ARecentFileInTheIndexWithdrawsTheOutputsUnderTheGuard()
    {
        CreateFullCache();
        TempDirectory.Age(Path.Combine(Child("o"), "payload.bin"), TimeSpan.FromDays(30));
        TempDirectory.Age(Path.Combine(Child("z"), "payload.bin"), TimeSpan.FromDays(30));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow));

        Assert.DoesNotContain(Child("o"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Child("h"), plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Child("z"), plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Child("o"), StringComparison.OrdinalIgnoreCase) && p.Withheld == Withholding.TooRecent);
        Assert.Contains(plan.Notes, n => n.Message.Contains("and its index alone", StringComparison.Ordinal));
    }

    /// <summary>
    /// With nothing recent the pair is offered under the guard, and a manifest written after the preview
    /// stops it at the clean, with nothing removed from either.
    /// </summary>
    [Fact]
    public async Task AManifestWrittenAfterThePreviewStopsTheOutputsUnderTheGuard()
    {
        CreateFullCache();

        foreach (var name in (string[])["h", "o"])
        {
            TempDirectory.Age(Path.Combine(Child(name), "payload.bin"), TimeSpan.FromDays(30));
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync(MinimumAge.Within(TimeSpan.FromDays(7), DateTime.UtcNow));
        Assert.Contains(Child("o"), plan.TargetedPaths);

        var arrived = Path.Combine(Child("h"), "arrived.txt");
        File.WriteAllText(arrived, "manifest");

        var result = await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(arrived));
        Assert.True(File.Exists(Path.Combine(Child("h"), "payload.bin")));
        Assert.True(File.Exists(Path.Combine(Child("o"), "payload.bin")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A Zig build started after the preview could write a manifest while the index is being removed, so
    /// the outputs are held back whole, and asserted standing.
    /// </summary>
    [Fact]
    public async Task AZigBuildStartedAfterThePreviewHoldsTheOutputsBack()
    {
        CreateFullCache();
        var inspector = new FakeProcessInspector();

        var provider = CreateProvider(inspector);
        var plan = await provider.PlanAsync();

        inspector.WithRunning("zig");
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(Child("h")));
        Assert.True(Directory.Exists(Child("o")));
        Assert.False(Directory.Exists(Child("b")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task WarnsWhenZigIsRunning()
    {
        Populate(Child("z"));

        var plan = await CreateProvider(new FakeProcessInspector("zls")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    /// <summary>
    /// Explore removes one folder at a time, so it may take the index or a cache that stands alone, and
    /// never the outputs, the packages, the root or anything unrecognised.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("o", false)]
    [InlineData(@"o\5d41402abc4b2a76b9719d911017c592", false)]
    [InlineData("p", false)]
    [InlineData("c", false)]
    [InlineData("h", true)]
    [InlineData("z", true)]
    [InlineData("b", true)]
    [InlineData("tmp", true)]
    public void ExploreMayRemoveOnlyWhatGoesSafelyOnItsOwn(string relative, bool allowed)
    {
        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(relative.Length == 0 ? Root : Path.Combine(Root, relative)).IsAllowed);
    }
}
