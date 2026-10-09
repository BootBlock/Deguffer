using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// One plan's several measurements carry one reason, and a walk that merely answered first must not
/// take the place of a fallback the user is owed a sentence about (§5.5).
/// </summary>
public sealed class CombinedFallbackTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(FallbackReason.WalkAnsweredFirst, FallbackReason.NotNtfsVolume, FallbackReason.NotNtfsVolume)]
    [InlineData(FallbackReason.WalkAnsweredFirst, FallbackReason.MasterFileTableIncomplete, FallbackReason.MasterFileTableIncomplete)]
    [InlineData(FallbackReason.NotElevated, FallbackReason.NotNtfsVolume, FallbackReason.NotElevated)]
    [InlineData(FallbackReason.None, FallbackReason.NotElevated, FallbackReason.NotElevated)]
    [InlineData(FallbackReason.None, FallbackReason.WalkAnsweredFirst, FallbackReason.WalkAnsweredFirst)]
    [InlineData(FallbackReason.WalkAnsweredFirst, FallbackReason.None, FallbackReason.WalkAnsweredFirst)]
    [InlineData(FallbackReason.None, FallbackReason.None, FallbackReason.None)]
    public void TheFirstReasonWithSomethingToSayPrevails(FallbackReason first, FallbackReason second, FallbackReason expected) =>
        Assert.Equal(expected, FallbackReasonText.Prevailing(first, second));

    /// <summary>
    /// The first path is walked while its volume's table is read, and the second falls back on a
    /// volume with no table. The plan says why the second was walked.
    /// </summary>
    [Fact]
    public async Task APlanWhoseFirstPathWasWalkedFirstStillSaysWhyALaterOneFellBack()
    {
        var quick = _temp.CreateDirectory("quick");
        var fallen = _temp.CreateDirectory("fallen");
        var scanner = new ScriptedScanner(new Dictionary<string, FallbackReason>
        {
            [quick] = FallbackReason.WalkAnsweredFirst,
            [fallen] = FallbackReason.NotNtfsVolume,
        });

        var plan = await new TargetsProvider(
            new FakeUserEnvironment(_temp.CreateDirectory("profile")),
            scanner,
            new DeletionTarget(quick, "Walked first"),
            new DeletionTarget(fallen, "Fell back")).PlanAsync();

        Assert.Equal(FallbackReason.NotNtfsVolume, plan.Fallback);
        Assert.Contains(plan.Notes, n => n.Message == FallbackReasonText.Describe(FallbackReason.NotNtfsVolume));
    }

    /// <summary>Answers every path with an empty folder, by the route the test names for it.</summary>
    private sealed class ScriptedScanner(IReadOnlyDictionary<string, FallbackReason> reasons) : IDirectoryScanner
    {
        public ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            new(ScanResult.Slow(ScanSize.Zero, reasons[path]));

        public ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default) =>
            MeasureAsync(path, ct: ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
            string name,
            string root,
            CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

        public void Invalidate()
        {
        }
    }

    /// <summary>A provider that plans exactly the targets it is handed, measured by the given scanner.</summary>
    private sealed class TargetsProvider(IUserEnvironment environment, IDirectoryScanner scanner, params DeletionTarget[] targets)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner)
    {
        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        public override string Id => "targets";

        public override string Name => "Targets";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

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
            var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct);

            return new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = steps,
                Notes = measured.Note is { } note ? [note] : [],
                Fallback = measured.Fallback,
            };
        }
    }
}
