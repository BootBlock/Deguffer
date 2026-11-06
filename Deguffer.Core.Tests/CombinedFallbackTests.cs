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
        public ScanTuner Tuning => ScanTuner.Shipped;

        public ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            ScanBounds bounds = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            new(ScanResult.Slow(ScanSize.Zero, reasons[path]));

        public ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked,
            MinimumAge keep = default,
            CancellationToken ct = default) =>
            new([.. asked.Select(question => ScanResult.Slow(ScanSize.Zero, reasons[question.Path]))]);

        public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
            new(new AgedScan(ScanResult.Slow(ScanSize.Zero, reasons[path]), ScanResult.Slow(ScanSize.Zero, reasons[path])));

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            MeasureManyAsync(asked, ct: ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
            DirectorySearch search,
            string root,
            CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

        public void Invalidate()
        {
        }

        public PassHold Hold() => PassHold.Nothing;
    }

    /// <summary>
    /// A plan's targets are measured in one call, so a walking scanner reads them all in one walk, and
    /// each step still carries its own target's figure rather than a neighbour's.
    /// </summary>
    [Fact]
    public async Task APlanMeasuresEveryTargetInOneCallAndPairsEachWithItsOwnFigure()
    {
        string[] targets = [.. new[] { 1000, 2000, 3000 }.Select(bytes => Path.GetDirectoryName(_temp.CreateFile(bytes, $"t{bytes}", "a.bin"))!)];
        var scanner = new FakeDirectoryScanner();

        var plan = await new TargetsProvider(
            new FakeUserEnvironment(_temp.CreateDirectory("profile")),
            scanner,
            [.. targets.Select(target => new DeletionTarget(target, "Measured"))]).PlanAsync();

        Assert.Equal(1, scanner.MeasureManyCalls);
        Assert.Equal(targets.Length, scanner.Measured.Count);
        Assert.Equal(
            [(targets[0], 1000L), (targets[1], 2000L), (targets[2], 3000L)],
            plan.Steps.OfType<DeleteDirectoryStep>().Select(step => (step.Path, step.Estimated.Logical)));
    }
}
