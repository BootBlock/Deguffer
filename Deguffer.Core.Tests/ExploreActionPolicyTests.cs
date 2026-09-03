using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1's refusals, asserted without a WinUI host — which is the whole reason the decision is a
/// Core type rather than a disabled context-menu item.
///
/// <para>Everything here runs against a synthetic Windows directory, synthetic program directories
/// and a synthetic profile. That is not a convenience: the rule that matters is that Explore never
/// reaches <c>C:\Windows</c>, and it has to be demonstrable on a machine where nobody may delete
/// anything in there.</para>
/// </summary>
public sealed class ExploreActionPolicyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes;

    public ExploreActionPolicyTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
        _volumes = new FakeVolumeInventory().With(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheWindowsDirectoryAndEverythingInItIsRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.WindowsDirectory).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.WindowsDirectory, "System32")).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.WindowsDirectory, "System32", "drivers", "etc")).IsAllowed);
    }

    /// <summary>
    /// §9's two exclusions by name. They are covered by the rule above, and naming them anyway is
    /// the point: §9 is enforced by nothing except not reaching those paths, so an assertion that
    /// says "we did not reach them" is what turns that into evidence.
    /// </summary>
    [Theory]
    [InlineData("WinSxS")]
    [InlineData("Installer")]
    public void TheSection9ExclusionsAreRefused(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_system.WindowsDirectory, name)).IsAllowed);
    }

    /// <summary>
    /// Both program directories. A rule that knew only the 64-bit one would allow half the
    /// installed software on the machine, which is the shape of hole nobody notices.
    /// </summary>
    [Fact]
    public void BothProgramDirectoriesAreRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.ProgramFiles).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramFiles, "Some Vendor", "bin")).IsAllowed);
        Assert.False(policy.MayRemove(_system.ProgramFilesX86).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramFilesX86, "Some Vendor")).IsAllowed);
    }

    [Fact]
    public void MachineWideApplicationDataIsRefused()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_system.ProgramData).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_system.ProgramData, "Some Vendor")).IsAllowed);
    }

    [Fact]
    public void AWholeDriveIsRefused()
    {
        Assert.False(Policy().MayRemove(_temp.Path).IsAllowed);
        Assert.False(Policy().MayRemove(@"C:\").IsAllowed);
    }

    /// <summary>
    /// The three entries that read as one rule: the profile is not a thing to remove, what the user
    /// keeps inside it is ordinary, and another account's profile is neither.
    /// </summary>
    [Fact]
    public void TheProfileItselfIsRefusedWhileWhatIsInsideItIsNot()
    {
        var policy = Policy();

        Assert.False(policy.MayRemove(_environment.UserProfile).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(_environment.UserProfile, "Downloads")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(_environment.UserProfile, "Downloads", "big.iso")).IsAllowed);
    }

    [Fact]
    public void AnotherAccountsProfileIsRefused()
    {
        var users = Path.GetDirectoryName(_environment.UserProfile)!;

        Assert.False(Policy().MayRemove(Path.Combine(users, "someone-else")).IsAllowed);
        Assert.False(Policy().MayRemove(Path.Combine(users, "someone-else", "Documents")).IsAllowed);
    }

    [Theory]
    [InlineData("System Volume Information")]
    [InlineData("$Recycle.Bin")]
    [InlineData("pagefile.sys")]
    [InlineData("swapfile.sys")]
    [InlineData("hiberfil.sys")]
    public void WhatWindowsKeepsAtAVolumeRootIsRefused(string name)
    {
        Assert.False(Policy().MayRemove(Path.Combine(_temp.Path, name)).IsAllowed);
    }

    [Fact]
    public void AToolRootIsNeverRemoved()
    {
        Assert.False(Policy(Gradle()).MayRemove(GradleRoot).IsAllowed);
    }

    /// <summary>
    /// §5.2's unrecognised case, which is the dangerous direction: an unknown thing must not be
    /// treated as safe. <c>gradle.properties</c> is the example §7.1 chose, and it may hold signing
    /// keys and credentials.
    /// </summary>
    [Theory]
    [InlineData("gradle.properties")]
    [InlineData("init.d")]
    [InlineData(@"init.d\company.gradle")]
    [InlineData("something-a-later-gradle-added")]
    public void AnUnrecognisedChildOfAToolRootIsRefused(string relative)
    {
        var verdict = Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, relative));

        Assert.False(verdict.IsAllowed);
        Assert.Contains("not something Deguffer recognises", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The first segment below the root decides, not the leaf. Asking about the leaf instead would
    /// refuse <c>caches\modules-2</c> and allow <c>init.d\company.gradle</c>, which is exactly
    /// backwards.
    /// </summary>
    [Theory]
    [InlineData("caches")]
    [InlineData(@"caches\modules-2")]
    [InlineData(@"caches\modules-2\files-2.1\org.example")]
    [InlineData("wrapper")]
    public void ARecognisedChildOfAToolRootTakesWhatIsUnderItToo(string relative)
    {
        Assert.True(Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, relative)).IsAllowed);
    }

    /// <summary>
    /// The profile is permitted below, and <c>.gradle</c> sits inside it. The permitting entry ends
    /// the structural table's search and not the §5.2 check that follows it — get that ordering
    /// wrong and every tool root in the user's own profile stops being protected, which is all of
    /// them.
    /// </summary>
    [Fact]
    public void BeingInsideThePermittedProfileDoesNotOverrideSection52()
    {
        Assert.True(LongPath.Contains(_environment.UserProfile, GradleRoot));
        Assert.False(Policy(Gradle()).MayRemove(Path.Combine(GradleRoot, "gradle.properties")).IsAllowed);
    }

    /// <summary>
    /// §7.1: "A path Explore does not recognise is unclassified, not safe." Most of a drive is in
    /// this state, and what the user is told about it must not be the word the tier model reserves
    /// for a thing a provider examined.
    /// </summary>
    [Fact]
    public void AnUnknownPathIsAllowedAndIsNeverDescribedAsSafe()
    {
        var verdict = Policy().MayRemove(Path.Combine(_environment.UserProfile, "Videos", "holiday.mp4"));

        Assert.True(verdict.IsAllowed);
        Assert.DoesNotContain("safe", verdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not classified", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path the rules cannot be applied to is refused rather than waved through. Every comparison
    /// in the policy is a prefix match on text, so a value that will not normalise would walk past
    /// the whole table.
    /// </summary>
    [Theory]
    [InlineData("not-a-full-path")]
    [InlineData(@"..\somewhere")]
    [InlineData("")]
    public void APathThatWillNotNormaliseIsRefused(string path)
    {
        Assert.False(Policy().MayRemove(path).IsAllowed);
    }

    /// <summary>
    /// The wiring, once, through a real provider: <see cref="ExploreActionPolicy.For"/> reads §5.2
    /// out of the providers rather than restating it, so a provider's own declaration is what
    /// Explore enforces.
    /// </summary>
    [Fact]
    public void ThePolicyReadsSection52OutOfTheProvidersThemselves()
    {
        var provider = new GradleCacheProvider(_environment);
        var policy = ExploreActionPolicy.For(_system, _environment, _volumes, [provider]);

        Assert.Equal(GradleRoot, provider.RootPath);
        Assert.False(policy.MayRemove(provider.RootPath).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(provider.RootPath, "gradle.properties")).IsAllowed);
        Assert.True(policy.MayRemove(Path.Combine(provider.RootPath, "caches")).IsAllowed);
    }

    private string GradleRoot => Path.Combine(_environment.UserProfile, ".gradle");

    private ToolRoot Gradle() =>
        ToolRoot.Of(GradleRoot, "Gradle's own folder.", GradleCacheProvider.DisposableChildren);

    private ExploreActionPolicy Policy(params ToolRoot[] toolRoots) =>
        ExploreActionPolicy.For(_system, _environment, _volumes, [new StubProvider(toolRoots)]);

    private sealed class StubProvider(IReadOnlyList<ToolRoot> roots) : ICleanupProvider
    {
        public string Id => "stub";

        public string Name => "Stub";

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public string WhatHappensOnNextUse => "Nothing.";

        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => roots;

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public void InvalidateCaches()
        {
        }

        public Task<Execution.CleanupPlan> PlanAsync(CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.CleanupResult> ExecuteAsync(
            Execution.CleanupPlan plan,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");

        public Task<Execution.VerificationResult> VerifyAsync(
            Execution.CleanupPlan plan, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists only to carry a tool-root declaration.");
    }
}
