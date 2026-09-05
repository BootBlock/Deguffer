using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Dart analysis server's byte store is path-based, so §5.2 carries the load here as it does
/// for Gradle: what must never appear in a plan matters more than what does. Every child of
/// <c>.dartServer</c> is a dot-named directory, and only two of the five are disposable, so an
/// over-broad rule would take the user's own settings with it and look correct doing so.
///
/// <para>§6.3 is deliberately not asserted here. This provider hands its paths to
/// <see cref="ChildDirectories"/> and to <c>DirectoryRemover</c>, and those two seams are where the
/// extended prefix is asserted on the path's <em>form</em> — see <c>LongPathTests</c> and
/// <c>DirectoryRemoverTests</c>. A deep-tree test written here would pass with
/// <see cref="LongPath.Extended"/> deleted outright, so it would prove nothing about this provider
/// that those tests do not already prove about the seams underneath it.</para>
/// </summary>
public sealed class DartAnalysisServerProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public DartAnalysisServerProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private DartAnalysisServerProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateServerRoot()
    {
        var root = Path.Combine(_environment.LocalAppData, ".dartServer");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheAnalysisServerNeverRan()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    [Fact]
    public async Task PlansTheByteStoreAndThePackageDetailsCacheWithTheirMeasuredSizes()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 8192);
        CreateAt(root, ".pub-package-details-cache", 1024);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(
            [Path.Combine(root, ".analysis-driver"), Path.Combine(root, ".pub-package-details-cache")],
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Fact]
    public async Task NeverTargetsTheDartServerRootDirectory()
    {
        CreateServerRoot();
        var provider = CreateProvider();
        CreateAt(provider.RootPath, ".analysis-driver", 1024);

        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(provider.RootPath, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path => Assert.NotEqual(
            provider.RootPath.TrimEnd(Path.DirectorySeparatorChar),
            path.TrimEnd(Path.DirectorySeparatorChar),
            StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>.prompts</c> is the <c>gradle.properties</c> of this root: it holds the user's own
    /// answers to the questions the server asks, it is never regenerated from anything, and it is a
    /// dot-named directory beside two dot-named directories that are removed.
    /// </summary>
    [Fact]
    public async Task NeverPlansThePromptsPreferencesBecauseTheyAreTheUsersOwnAnswers()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 1024);
        var prompts = CreateAt(root, ".prompts", 64);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(prompts, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.All(plan.TargetedPaths, path =>
            Assert.False(IsAtOrUnder(prompts, path), $"{path} would have taken .prompts with it."));

        // It is not merely absent from the plan — it is asserted to survive (§5.6).
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(prompts, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown child treated as safe. <c>.instrumentation</c> is
    /// the live case: the issue that scoped this provider measured it at zero, and a rule that
    /// reasoned from size or from the leading dot would have swept it up.
    /// </summary>
    [Theory]
    [InlineData(".instrumentation")]
    [InlineData(".plugin_manager")]
    [InlineData(".a-child-a-later-sdk-adds")]
    public async Task UnrecognisedChildIsClassifiedTier4AndLeftAlone(string name)
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 1024);
        var unknown = CreateAt(root, name, 8192);

        Assert.Equal(
            SafetyTier.DoNotTouch,
            DartAnalysisServerProvider.DisposableChildren.Classify(name).Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.Notes, n => n.Message.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The provider is Tier 1 throughout, so a child declared at any other offerable tier would be
    /// offered under a promise the row does not make.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var name in DartAnalysisServerProvider.DisposableChildren.DisposableNames)
        {
            Assert.Equal(provider.Tier, DartAnalysisServerProvider.DisposableChildren.Classify(name).Tier);
        }
    }

    /// <summary>
    /// Redirecting the store onto another drive with a junction is how a developer keeps 3 GB off a
    /// small system disk, and the enumeration never classifies the directory it is handed — so a
    /// junctioned root would hand back the far side's ordinary children, target the recognised ones,
    /// and pass every §5.6 assertion, because each survivor named here resolves through the same
    /// link.
    /// </summary>
    [Fact]
    public async Task DeclinesARootThatIsItselfALink()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = CreateAt(outside, ".analysis-driver", 4096);
        Directory.CreateSymbolicLink(Path.Combine(_environment.LocalAppData, ".dartServer"), outside);

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

    /// <summary>
    /// The same decline one level in, and with nothing left to offer. The byte store is the whole
    /// size of this row, so a plan that is present, empty and about nothing that was looked at must
    /// not be rendered as "Already clear".
    /// </summary>
    [Fact]
    public async Task ARootWhoseEveryRecognisedChildIsALinkIsNotCalledClear()
    {
        var root = CreateServerRoot();

        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = CreateAt(outside, "payload", 65536);

        Directory.CreateSymbolicLink(Path.Combine(root, ".analysis-driver"), outside);
        Directory.CreateSymbolicLink(Path.Combine(root, ".pub-package-details-cache"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// An unrecognised child is read, classified and ruled out, so a root holding only those was
    /// examined in full and its zero is the whole story.
    /// </summary>
    [Fact]
    public async Task ARootHoldingOnlyUnrecognisedChildrenIsStillCalledClear()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".instrumentation", 64);
        CreateAt(root, ".prompts", 64);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.False(plan.WasNotExamined);
        Assert.False(plan.HasUnreadableRoot);
    }

    /// <summary>
    /// §5.3. An analysis server inside an open editor holds the byte store, so the warning is what
    /// tells the user why an access-denied is about to be ordinary rather than a failure.
    /// </summary>
    [Fact]
    public async Task WarnsWhenAnAnalysisServerIsHoldingTheByteStoreOpen()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 1024);

        var provider = new DartAnalysisServerProvider(
            _environment, new FakeProcessRunner(), new FakeProcessInspector("dart"));
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfAProtectedPathVanished()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 1024);
        var prompts = CreateAt(root, ".prompts", 64);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad rule §5.6 exists to catch.
        Directory.Delete(prompts, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(prompts, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecutingRemovesTheCachesAndLeavesTheServersOwnStateStanding()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 4096);
        CreateAt(root, ".pub-package-details-cache", 4096);
        var prompts = CreateAt(root, ".prompts", 64);
        var instrumentation = CreateAt(root, ".instrumentation", 64);
        var plugins = CreateAt(root, ".plugin_manager", 64);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(Path.Combine(root, ".analysis-driver")));
        Assert.False(Directory.Exists(Path.Combine(root, ".pub-package-details-cache")));

        // §5.6, the negative: the root and every child the plan declined are still there.
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(prompts));
        Assert.True(Directory.Exists(instrumentation));
        Assert.True(Directory.Exists(plugins));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The root is found by name, and a listing right is separate from a traverse right — so a
    /// refusal here yields a plan with no steps, which the shell would otherwise render as "Already
    /// clear", a claim about a folder nobody read.
    /// </summary>
    [Fact]
    public async Task ARootThatWillNotBeListedIsSaidSoRatherThanLeftLookingAlreadyClear()
    {
        var root = CreateServerRoot();
        CreateAt(root, ".analysis-driver", 4096);

        using var denied = new DeniedDirectory(root);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(root));
        Assert.Empty(plan.TargetedPaths);

        // Windows refused this listing, so the answer the user needs is permissions. The link test
        // above is Deguffer's own decision, where there are no permissions to check.
        Assert.False(plan.WasNotExamined);
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
