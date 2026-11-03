using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Ivy keeps what <c>publishLocal</c> built beside its download cache, so what must never appear in
/// a plan matters more than what does.
/// </summary>
public sealed class IvyCacheProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public IvyCacheProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Home => Path.Combine(_environment.UserProfile, ".ivy2");

    private IvyCacheProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(_environment, new FakeProcessRunner(), inspector ?? FakeProcessInspector.NothingRunning);

    [Fact]
    public async Task ReportsNotPresentWhenIvyNeverRan()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansTheCacheAndNeverTheHome()
    {
        CreateAt(Home, @"cache\org.scala-lang", 4096);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Path.Combine(Home, "cache")], plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Home, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    /// <summary>
    /// §5.2. <c>local</c> holds what <c>publishLocal</c> built, which exists on no remote. It is named
    /// Tier 4 rather than left to the unrecognised case, so the user is told what it is.
    /// </summary>
    [Fact]
    public async Task NeverPlansWhatPublishLocalPublished()
    {
        CreateAt(Home, "cache", 4096);
        var local = CreateAt(Home, @"local\com.example\library", 8192);

        Assert.Equal(SafetyTier.DoNotTouch, IvyCacheProvider.Children.Classify("local").Tier);

        var plan = await CreateProvider().PlanAsync();

        Assert.All(plan.TargetedPaths, path => Assert.False(LongPath.Contains(path, local), $"{path} would take {local} with it."));
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(Path.Combine(Home, "local"), StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present
            && p.Reason.Contains("publishLocal", StringComparison.Ordinal));
        Assert.Single(plan.Notes, n => n.Message.Contains("'local'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnrecognisedChildIsTier4AndProtected()
    {
        CreateAt(Home, "cache", 1024);
        var unknown = CreateAt(Home, "something-else", 4096);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(unknown, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(unknown, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
    }

    [Fact]
    public async Task ExecutingRemovesTheCacheAndLeavesTheLocalArtefactsAndLoginStanding()
    {
        CreateAt(Home, "cache", 4096);
        var local = CreateAt(Home, "local", 4096);
        var credentials = Path.Combine(Home, ".credentials");
        File.WriteAllText(credentials, "realm=Example\nhost=repo.example.test\nuser=someone\npassword=<YOUR_PASSWORD>");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(credentials, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(Home, "cache")));
        Assert.True(Directory.Exists(local));
        Assert.True(File.Exists(credentials));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheLocalArtefactsVanished()
    {
        CreateAt(Home, "cache", 1024);
        var local = CreateAt(Home, "local", 1024);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(local, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Subject.Equals(local, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task DeclinesAHomeThatIsItselfALink(DirectoryLinkKind kind)
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var stranger = CreateAt(outside, "cache", 4096);
        DirectoryLink.Create(kind, Home, outside);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(Directory.Exists(stranger));
        Assert.True(plan.WasNotExamined);
    }

    [Fact]
    public async Task WarnsWhenAJavaProcessMayBeReadingTheCache()
    {
        CreateAt(Home, "cache", 1024);

        var plan = await CreateProvider(new FakeProcessInspector("java")).PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        CreateAt(Home, "cache", 4096);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData(@"local\com.example", false)]
    [InlineData(".credentials", false)]
    [InlineData("cache", true)]
    [InlineData(@"cache\org.scala-lang", true)]
    public void ExploreRefusesEverythingInTheHomeButTheCache(string relative, bool allowed)
    {
        var path = Path.Combine(Home, relative);

        if (relative == ".credentials")
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(path, "realm=Example");
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        var policy = new ExploreActionPolicy(
            ProtectedRegions.For(new FakeSystemDirectories(_temp.Path), _environment),
            CreateProvider().ToolRoots,
            volumes: new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(path).IsAllowed);
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
