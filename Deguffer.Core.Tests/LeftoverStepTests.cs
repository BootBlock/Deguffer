using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A step that frees no bytes and still removes something, and the rule that decides whether it may
/// be chosen.
///
/// <para>An empty folder its tool re-creates at the next launch frees nothing that lasts, and offering
/// it would keep a row at "Ready to clean" permanently. An empty folder nothing will re-create — what a
/// session leaves after it has ended — is the whole of what its provider is for. Only the provider can
/// tell the two apart, so only a step its provider declared a leftover may be chosen on its entries
/// alone.</para>
/// </summary>
public sealed class LeftoverStepTests : IDisposable
{
    private const string SomeFolder = @"C:\Users\testuser\.tool\folder";

    private static readonly ScanSize OneEmptyFolder = new(0, 0, Entries: 1);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public LeftoverStepTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnEmptyFolderItsToolReCreatesIsNotWorthChoosing() =>
        Assert.False(new DeleteDirectoryStep(SomeFolder, "A cache") { Estimated = OneEmptyFolder }.RemovesSomething);

    [Fact]
    public void AnEmptyFolderDeclaredALeftoverIsWorthChoosing() =>
        Assert.True(new DeleteDirectoryStep(SomeFolder, "A leftover")
        {
            Estimated = OneEmptyFolder,
            IsLeftover = true,
        }.RemovesSomething);

    /// <summary>
    /// A leftover whose every entry stays — kept by the guard, or refused — removes nothing. A tick on
    /// it would be a clean that removes nothing and reports that it did.
    /// </summary>
    [Fact]
    public void ALeftoverWithNothingLeftToRemoveIsNotWorthChoosing() =>
        Assert.False(new DeleteDirectoryStep(SomeFolder, "A leftover")
        {
            Estimated = ScanSize.Zero,
            IsLeftover = true,
        }.RemovesSomething);

    /// <summary>
    /// §5.1: a tool's own command decides what it removes, so entries under the path it is measured
    /// against say nothing about what it will free.
    /// </summary>
    [Fact]
    public void ACommandIsChosenOnWhatItFreesAlone() =>
        Assert.False(new RunCommandStep("tool", "clean", "Clean") { Estimated = new ScanSize(0, 0, Entries: 500) }
            .RemovesSomething);

    [Fact]
    public void AnythingThatFreesBytesIsWorthChoosing()
    {
        Assert.True(new DeleteDirectoryStep(SomeFolder, "A cache") { Estimated = ScanSize.FromLengths(1) }.RemovesSomething);
        Assert.True(new RunCommandStep("tool", "clean", "Clean") { Estimated = ScanSize.FromLengths(1) }.RemovesSomething);
    }

    [Fact]
    public void ARowOfEmptyLeftoversHasSomethingToRemoveAndStartsTickedAtTier1()
    {
        var finding = FindingOf(
            SafetyTier.RegenerableCache,
            new DeleteDirectoryStep(SomeFolder, "A leftover") { Estimated = OneEmptyFolder, IsLeftover = true });

        Assert.True(finding.HasSomethingToRemove);
        Assert.True(finding.IsPreSelectedByDefault);
    }

    [Fact]
    public void ARowOfEmptyFoldersItsToolReCreatesHasNothingToRemove()
    {
        var finding = FindingOf(
            SafetyTier.RegenerableCache,
            new DeleteDirectoryStep(SomeFolder, "A cache") { Estimated = OneEmptyFolder });

        Assert.False(finding.HasSomethingToRemove);
        Assert.False(finding.IsPreSelectedByDefault);
    }

    [Fact]
    public async Task APlannedLeftoverCarriesItsDeclarationAndItsCount()
    {
        var leftover = _temp.CreateDirectory("state", "session");

        var provider = new TargetsProvider(
            _environment, SafetyTier.RegenerableCache, new DeletionTarget(leftover, "A leftover", IsLeftover: true));

        var step = Assert.IsType<DeleteDirectoryStep>(Assert.Single((await provider.PlanAsync()).Steps));

        Assert.True(step.IsLeftover);
        Assert.Equal(1, step.Estimated.Entries);
        Assert.True(step.RemovesSomething);
    }

    /// <summary>
    /// A folder cleared in place stays standing, so its own entry is not among what the clearing
    /// takes. Counted, an empty folder cleared in place would claim one entry and remove none.
    /// </summary>
    [Fact]
    public async Task AFolderClearedInPlaceDoesNotCountItself()
    {
        var scratch = _temp.CreateDirectory("scratch");
        _temp.CreateDirectory("scratch", "empty-one");
        _temp.CreateDirectory("scratch", "empty-two");

        var provider = new TargetsProvider(
            _environment,
            SafetyTier.RegenerableWithCost,
            new DeletionTarget(scratch, "Scratch", Kind: TargetKind.DirectoryContents));

        var step = Assert.Single((await provider.PlanAsync()).Steps);

        Assert.Equal(2, step.Estimated.Entries);
    }

    /// <summary>
    /// Every deletion is measured in entries, and only a leftover's are part of what choosing it
    /// reclaims. A cache folder's count would otherwise put "1 item" against every empty cache and
    /// every total beside it.
    /// </summary>
    [Fact]
    public void OnlyALeftoversEntriesArePartOfWhatChoosingItReclaims()
    {
        var measured = new ScanSize(4096, 4096, Entries: 30);

        Assert.Equal(30, new DeleteDirectoryStep(SomeFolder, "A leftover") { Estimated = measured, IsLeftover = true }.Reclaim.Entries);
        Assert.Equal(0, new DeleteDirectoryStep(SomeFolder, "A cache") { Estimated = measured }.Reclaim.Entries);
        Assert.Equal(0, new RunCommandStep("tool", "clean", "Clean") { Estimated = measured }.Reclaim.Entries);
        Assert.Equal(4096, new DeleteDirectoryStep(SomeFolder, "A cache") { Estimated = measured }.Reclaim.Reclaimable);
    }

    private Finding FindingOf(SafetyTier tier, CleanupStep step)
    {
        var provider = new TargetsProvider(_environment, tier);

        return new Finding(provider, IsPresent: true, new CleanupPlan
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Tier = tier,
            WhatHappensOnNextUse = provider.WhatHappensOnNextUse,
            Steps = [step],
        });
    }

    /// <summary>
    /// A provider that plans exactly the targets it is handed, so what the base class makes of a
    /// target can be asserted without any one tool's rules in the way.
    /// </summary>
    private sealed class TargetsProvider(IUserEnvironment environment, SafetyTier tier, params DeletionTarget[] targets)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, new FakeDirectoryScanner())
    {
        public override string Id => "targets";

        public override string Name => "Targets";

        public override SafetyTier Tier => tier;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderDescription Description { get; } = new()
        {
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Plans the targets it is handed.",
            Recommendation = "None.",
        };

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
        {
            var (steps, _) = await PlanDeletionsAsync(targets, keep, ct);

            return new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = steps,
            };
        }
    }
}
