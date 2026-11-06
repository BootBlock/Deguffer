using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A target a provider asks to have dated one level down (<see cref="DeletionTarget.DatedOneLevelDown"/>)
/// is dated by the measurement that sizes it, and its step carries its own age.
///
/// <para>The rows that date their targets this way are the ones with the most of them: every
/// <c>obj</c> folder and <c>node_modules</c> of a source root. Their measurements are taken together,
/// and pairing each age with its step is a pairing by position, which is the shape that hands one
/// directory's age to another.</para>
/// </summary>
public sealed class TargetDatingTests : IDisposable
{
    private static readonly DateTime LongAgo = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private TargetsProvider Provider(params DeletionTarget[] targets) =>
        new(new FakeUserEnvironment(_temp.CreateDirectory("profile")), new FakeDirectoryScanner(), targets);

    /// <summary>
    /// Each step dated one level down carries its own directory's age, a refused directory's step
    /// carries none, and a target that brought an age of its own, or asked for none, keeps what it
    /// brought. The ages differ by a day a directory, so a neighbour's age is a failure.
    /// </summary>
    [Fact]
    public async Task EveryStepDatedOneLevelDownCarriesItsOwnDirectorysAge()
    {
        const int Count = 32;
        const int Refused = 17;

        var targets = new List<DeletionTarget>();
        var expected = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < Count; index++)
        {
            var directory = _temp.CreateDirectory($"project-{index:D2}", "obj");
            var dll = _temp.CreateFile(16, $"project-{index:D2}", "obj", "Example.dll");

            var written = LongAgo.AddDays(index);
            EntryTime.Set(dll, written);
            EntryTime.Set(directory, LongAgo);

            targets.Add(new DeletionTarget(directory, "Build output", DatedOneLevelDown: true));
            expected[directory] = index == Refused ? null : written;
        }

        var given = LongAgo.AddYears(-1);
        var dated = _temp.CreateDirectory("dated-by-its-provider");
        targets.Add(new DeletionTarget(dated, "Dated by its provider", given));
        expected[dated] = given;

        var undated = _temp.CreateDirectory("a-whole-cache");
        targets.Add(new DeletionTarget(undated, "A whole cache"));
        expected[undated] = null;

        using var denied = new DeniedDirectory(targets[Refused].Path);

        var plan = await Provider([.. targets]).PlanAsync();

        Assert.Equal(expected.Count, plan.Steps.Count);

        foreach (var step in plan.Steps.Cast<DeleteStep>())
        {
            Assert.Equal(expected[step.Path], step.LastWritten);
        }
    }

    /// <summary>
    /// A step dated one level down carries the age its own measurement read, and nothing else is read
    /// to date it: the target's top level is listed once, by the measurement, rather than once for its
    /// size and again for its age.
    ///
    /// <para>The measurement here reports an age no directory on the disk has, so a step carrying the
    /// disk's age was dated by a listing of its own.</para>
    /// </summary>
    [Fact]
    public async Task AStepDatedOneLevelDownTakesTheAgeItsMeasurementRead()
    {
        var reported = new DateTime(2099, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var directory = _temp.CreateDirectory("project", "obj");
        EntryTime.Set(_temp.CreateFile(16, "project", "obj", "Example.dll"), LongAgo);
        EntryTime.Set(directory, LongAgo);

        var scanner = new ReportingAnAge(new FakeDirectoryScanner(), reported);
        var provider = new TargetsProvider(
            new FakeUserEnvironment(_temp.CreateDirectory("profile")),
            scanner,
            new DeletionTarget(directory, "Build output", DatedOneLevelDown: true));

        var plan = await provider.PlanAsync();

        Assert.Equal(reported, Assert.Single(plan.Steps.Cast<DeleteStep>()).LastWritten);
        Assert.Equal([directory], scanner.Measured);
    }

    /// <summary>A file has no entries to be dated by, so asking for it is a defect in the provider.</summary>
    [Fact]
    public async Task AFileCannotBeDatedOneLevelDown()
    {
        var file = _temp.CreateFile(16, "MEMORY.DMP");

        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new DeletionTarget(file, "A dump", Kind: TargetKind.File, DatedOneLevelDown: true)).PlanAsync());

        Assert.Contains(file, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A target that brought an age of its own and asked to be dated as well would have one answer
    /// silently replace the other, so the plan refuses it.
    /// </summary>
    [Fact]
    public async Task ATargetCannotBringAnAgeAndAskToBeDated()
    {
        var directory = _temp.CreateDirectory("obj");

        await Assert.ThrowsAsync<ArgumentException>(
            () => Provider(new DeletionTarget(directory, "Build output", LongAgo, DatedOneLevelDown: true)).PlanAsync());
    }

    /// <summary>
    /// A scanner whose every measurement reports <paramref name="reported"/> as the path's age, whatever is
    /// on the disk, and which records each path it was asked to measure.
    /// </summary>
    private sealed class ReportingAnAge(FakeDirectoryScanner inner, DateTime reported) : IDirectoryScanner
    {
        public ScanTuner Tuning => inner.Tuning;

        public IReadOnlyCollection<string> Measured => inner.Measured;

        public async ValueTask<ScanResult> MeasureAsync(
            string path,
            MinimumAge keep = default,
            ScanBounds bounds = default,
            IProgress<ScanSize>? progress = null,
            CancellationToken ct = default) =>
            (await inner.MeasureAsync(path, keep, bounds, progress, ct)) with { LastWritten = reported };

        public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked,
            MinimumAge keep = default,
            CancellationToken ct = default) =>
            [.. (await inner.MeasureManyAsync(asked, keep, ct)).Select(result => result with { LastWritten = reported })];

        public ValueTask<AgedScan> MeasureAgedAsync(string path, MinimumAge age, CancellationToken ct = default) =>
            inner.MeasureAgedAsync(path, age, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            inner.MeasureFromDiskAsync(asked, ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
            DirectorySearch search,
            string root,
            CancellationToken ct = default) =>
            inner.TryFindDirectoriesAsync(search, root, ct);

        public void Invalidate() => inner.Invalidate();

        public PassHold Hold() => inner.Hold();
    }
}
