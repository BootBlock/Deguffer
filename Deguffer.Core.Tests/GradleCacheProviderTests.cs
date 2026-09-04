using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Gradle is the path-based provider, so it carries the load for §5.2. These are mostly negative
/// tests: what must never appear in a plan matters more than what does.
/// </summary>
public sealed class GradleCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public GradleCacheProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private GradleCacheProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateGradleHome()
    {
        var root = Path.Combine(_environment.UserProfile, ".gradle");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenGradleWasNeverInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansCachesAndWrapperWithTheirMeasuredSizes()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);
        CreateAt(root, "wrapper", 2048);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [Path.Combine(root, "caches"), Path.Combine(root, "wrapper")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Fact]
    public async Task NeverTargetsTheGradleRootDirectory()
    {
        CreateGradleHome();
        var provider = CreateProvider();
        CreateAt(provider.RootPath, "caches", 1024);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.RootPath, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path => Assert.NotEqual(
            provider.RootPath.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeverPlansGradlePropertiesBecauseItMayHoldSigningKeys()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "signing.keyId=DEADBEEF");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(properties, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path =>
            Assert.False(IsAtOrUnder(properties, path), $"{path} would have taken gradle.properties with it."));

        // It is not merely absent from the plan — it is asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(properties, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    [Fact]
    public async Task UnrecognisedChildIsClassifiedTier4AndLeftAlone()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var unknown = CreateAt(root, "daemon", 8192);

        Assert.Equal(SafetyTier.DoNotTouch, GradleCacheProvider.DisposableChildren.Classify("daemon").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains("daemon", StringComparison.Ordinal));
    }

    /// <summary>
    /// Moving <c>.gradle</c> onto another drive with a junction is common, and the enumeration
    /// never classifies the directory it is handed — so a junctioned root would hand back the far
    /// side's ordinary children, target the recognised ones, and pass every §5.6 assertion, because
    /// each survivor named here resolves through the same link.
    ///
    /// <para>What the decline leaves behind is a row the shell must not call clear. The probe
    /// follows the link, so the row is present with nothing to reclaim, and the caches on the far
    /// side are routinely the largest thing Deguffer would have found on the machine.</para>
    /// </summary>
    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = CreateAt(outside, "caches", 4096);
        Directory.CreateSymbolicLink(Path.Combine(_environment.UserProfile, ".gradle"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.Contains(plan.Notes, n => n.Message.Contains("link to somewhere else", StringComparison.Ordinal));

        // Not HasUnreadableRoot: Windows refused nothing here. Deguffer declined, and the two
        // states send the reader to different places.
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    [Fact]
    public async Task WarnsWhenAGradleProcessIsHoldingTheCacheOpen()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);

        var provider = new GradleCacheProvider(_environment, new FakeProcessRunner(), new FakeProcessInspector("java"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfAProtectedPathVanished()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 1024);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "org.gradle.jvmargs=-Xmx2g");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        File.Delete(properties);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(properties, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesConfigStanding()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);
        CreateAt(root, "wrapper", 4096);
        var properties = Path.Combine(root, "gradle.properties");
        File.WriteAllText(properties, "org.gradle.caching=true");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "caches")));
        Assert.False(Directory.Exists(Path.Combine(root, "wrapper")));

        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(properties));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The root is found by name, and a listing right is separate from a traverse right — so a
    /// refusal here yields a plan with no steps, and used to carry no sentence about it either. The
    /// shell renders that as "Already clear", which is a claim about a folder nobody read.
    /// </summary>
    [Fact]
    public async Task ARootThatWillNotBeListedIsSaidSoRatherThanLeftLookingAlreadyClear()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);

        using var denied = new DeniedDirectory(root);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(root));
        Assert.Empty(plan.TargetedPaths);

        // The other half of the pair, kept apart: Windows refused this listing, so the answer the
        // user needs is permissions. The sibling test above is Deguffer's own decision, where there
        // are no permissions to check.
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>
    /// A root whose attributes cannot be read is not reported as a link — and is reported as absent,
    /// which is a different wrong sentence and is pinned here rather than left to be discovered.
    ///
    /// <para><see cref="LongPath.IsReparsePoint"/> fails closed, which is right for a predicate
    /// guarding a deletion and would be wrong to render: every provider here turns a true into
    /// "it is a link to somewhere else", a specific claim about the machine rather than an
    /// admission that Deguffer could not tell. It never gets the chance. The only access rules that
    /// break the attribute read break the existence check identically — see
    /// <see cref="LongPathTests.FailsClosedOnAPathItCannotReadWhileTheExistenceCheckAheadOfItFailsToo"/>
    /// — and every such provider probes by name before it classifies, so it takes the absent branch
    /// first. Gradle stands for all eleven of them here because they share that shape.</para>
    ///
    /// <para><b>The absent branch is not right either.</b> "Gradle is not installed for this user"
    /// is asserted about a directory that is on disk with a 4 KB cache inside it, and
    /// <see cref="ICleanupProvider.IsPresentAsync"/> denies it on the same evidence — which is
    /// exactly the contradiction <see cref="UnreadableRoot.WhyNothingWasPlanned"/> was written to
    /// end one refusal over, where a root that will not be <em>listed</em> is reached by a probe
    /// that traverses to it. Fixing it means <see cref="LongPath.DirectoryExists"/> answering in
    /// three states across every root probe in Core, which is its own piece of work:
    /// <c>docs/todo/after-the-scanner.md</c> item 8. This test characterises today's behaviour so
    /// that work has something to change, and so nobody reads the link half as a clean bill of
    /// health.</para>
    /// </summary>
    [Fact]
    public async Task ARootWhoseAttributesCannotBeReadIsCalledAbsentRatherThanALink()
    {
        var root = CreateGradleHome();
        CreateAt(root, "caches", 4096);

        using var denied = DeniedDirectory.WithUnreadableAttributes(root);

        var provider = CreateProvider();

        // The directory is there with content in it, and the attribute read is refused — so the
        // fail-closed predicate would answer "link" if anything asked it.
        Assert.True(Directory.Exists(Path.Combine(root, "caches")));
        Assert.True(LongPath.IsReparsePoint(root));

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(plan.Notes, n => n.Message.Contains("link", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(plan.TargetedPaths);

        // What it says instead, and what it will stop saying when item 8 lands. The sibling test
        // above asserts IsPresentAsync is true for a root that merely will not be listed; here the
        // presence probe is refused as well, and the plan agrees with it for the wrong reason.
        Assert.False(await provider.IsPresentAsync());
        Assert.Contains(plan.Notes, n => n.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase));
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>Create <paramref name="child"/> under the root holding one file of the given size.</summary>
    private static string CreateAt(string root, string child, int bytes)
    {
        var directory = Path.Combine(root, child);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    private static bool IsAtOrUnder(string candidate, string ancestor) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
