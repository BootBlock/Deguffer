using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The guard on recently changed files applied to a plan's single files: a step whose whole subject is
/// one recent file is withdrawn, and the file is proved to survive. The plan takes the measurement's
/// verdict where it was given under the guard in force, and reads the file where it cannot.
///
/// <para>Where a test needs the measurement and the disk to disagree, the scanner rewrites the reading
/// the real walk took. That disagreement is what shows which of the two the plan believed.</para>
/// </summary>
public sealed class RecentFileStepTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private static MinimumAge Hours8 => MinimumAge.WithinHours(8, DateTime.UtcNow);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A file the measurement kept is withdrawn on the measurement's word. The disk says the file is
    /// old, so a plan that read the file a further time to ask the guard again would offer it.
    /// </summary>
    [Fact]
    public async Task AFileTheMeasurementKeptIsWithdrawnWithoutReadingItAgain()
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "logs", "tool.log"), TimeSpan.FromDays(30));

        var plan = await Plan(file, Hours8, reading => reading with { FileKept = true, WithheldRecent = true, Size = ScanSize.Zero });

        Assert.Empty(plan.Steps);
        AssertWithheldAsRecent(plan, file);
    }

    /// <summary>
    /// A verdict given under the user's guard does not answer the provider's stricter floor. The file
    /// is two days old: the user's eight hours leave it, and the floor's thirty days keep it, so the
    /// plan reads it under the floor and withdraws it.
    /// </summary>
    [Fact]
    public async Task AVerdictUnderTheUsersGuardDoesNotAnswerAStricterFloor()
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "logs", "tool.log"), TimeSpan.FromDays(2));
        var floor = MinimumAge.Within(TimeSpan.FromDays(30), DateTime.UtcNow);

        var plan = await new TargetsProvider(Profile(), new FakeDirectoryScanner(), Target(file)) { Floor = floor }
            .PlanAsync(Hours8);

        Assert.Empty(plan.Steps);
        Assert.Equal(floor, plan.Keep);
        AssertWithheldAsRecent(plan, file);
    }

    /// <summary>The same floor offers a file older than it, so the read above is a reading of the age and not a refusal to offer.</summary>
    [Fact]
    public async Task AFileOlderThanTheFloorIsStillOffered()
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "logs", "tool.log"), TimeSpan.FromDays(40));
        var floor = MinimumAge.Within(TimeSpan.FromDays(30), DateTime.UtcNow);

        var plan = await new TargetsProvider(Profile(), new FakeDirectoryScanner(), Target(file)) { Floor = floor }
            .PlanAsync(Hours8);

        Assert.Equal(file, Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps)).Path);
    }

    /// <summary>
    /// A file whose measurement gave no verdict is read: a scanner that judged nothing about the file
    /// leaves the plan to ask, and a recent file is still withdrawn.
    /// </summary>
    [Fact]
    public async Task AFileWithNoVerdictIsReadAndARecentOneWithdrawn()
    {
        var file = _temp.CreateFile(4096, "logs", "tool.log");

        var plan = await Plan(file, Hours8, reading => reading with { FileKept = null, WithheldRecent = false });

        Assert.Empty(plan.Steps);
        AssertWithheldAsRecent(plan, file);
    }

    /// <summary>
    /// A link written just now is withdrawn, though the file it points at is old: the removal judges a
    /// link by its own times, and the measurement said so.
    /// </summary>
    [Fact]
    public async Task ARecentlyWrittenLinkIsWithdrawn()
    {
        var target = TempDirectory.Age(_temp.CreateFile(8192, "elsewhere", "old.bin"), TimeSpan.FromDays(30));
        var link = Path.Combine(_temp.CreateDirectory("logs"), "recent-link.bin");
        SymbolicLink.ToFile(link, target);

        var plan = await new TargetsProvider(Profile(), new FakeDirectoryScanner(), Target(link)).PlanAsync(Hours8);

        Assert.Empty(plan.Steps);
        AssertWithheldAsRecent(plan, link);
        Assert.True(File.Exists(target));
    }

    /// <summary>
    /// A recent mail store is withheld as the store it is, never as a recent file: §9 leaves it
    /// whatever its age, and the plan says so.
    /// </summary>
    [Fact]
    public async Task ARecentMailStoreIsWithheldAsAStore()
    {
        var store = _temp.CreateFile(8192, "Documents", "archive.pst");

        var plan = await new TargetsProvider(Profile(), new FakeDirectoryScanner(), Target(store)).PlanAsync(Hours8);

        Assert.Empty(plan.Steps);
        Assert.Equal(Withholding.MailStore, Assert.Single(plan.ProtectedPaths, p => p.Path == store).Withheld);
    }

    /// <summary>An old file under the user's guard stays offered, and carries the verdict its measurement gave.</summary>
    [Fact]
    public async Task AnOldFileIsOfferedWithTheVerdictOfItsMeasurement()
    {
        var file = TempDirectory.Age(_temp.CreateFile(4096, "logs", "tool.log"), TimeSpan.FromDays(30));
        var guard = Hours8;

        var plan = await new TargetsProvider(Profile(), new FakeDirectoryScanner(), Target(file)).PlanAsync(guard);

        var step = Assert.IsType<DeleteFileStep>(Assert.Single(plan.Steps));
        Assert.Equal(new FileAgeVerdict(guard, Keeps: false), step.AgeVerdict);
        Assert.Equal(4096, step.Estimated.Reclaimable);
    }

    private Task<CleanupPlan> Plan(string file, MinimumAge keep, Func<ScanResult, ScanResult> rewrite) =>
        new TargetsProvider(Profile(), new RewritingScanner(rewrite), Target(file)).PlanAsync(keep);

    private FakeUserEnvironment Profile() => new(_temp.CreateDirectory("profile"));

    private static DeletionTarget Target(string file) => new(file, "A file a test offers.", Kind: TargetKind.File);

    /// <summary>
    /// §5.6: the withdrawn file is protected as too recent, so the run proves it is still there, and the
    /// file is untouched by planning.
    /// </summary>
    private static void AssertWithheldAsRecent(CleanupPlan plan, string file)
    {
        Assert.Equal(Withholding.TooRecent, Assert.Single(plan.ProtectedPaths, p => p.Path == file).Withheld);
        Assert.True(File.Exists(file));
    }

    /// <summary>The real walk's readings, each rewritten by <paramref name="rewrite"/> before the plan sees it.</summary>
    private sealed class RewritingScanner(Func<ScanResult, ScanResult> rewrite) : IDirectoryScanner
    {
        private static readonly ParallelEnumerationScanner Walk = ParallelEnumerationScanner.Default;

        public ScanTuner Tuning => ScanTuner.Shipped;

        public async ValueTask<ScanResult> MeasureAsync(
            string path, MinimumAge keep = default, ScanBounds bounds = default, IProgress<ScanSize>? progress = null, CancellationToken ct = default) =>
            rewrite(await Walk.MeasureAsync(path, keep, bounds, progress, ct));

        public async ValueTask<IReadOnlyList<ScanResult>> MeasureManyAsync(
            IReadOnlyList<ScanRequest> asked, MinimumAge keep = default, IProgress<ScanSize>? progress = null, CancellationToken ct = default) =>
            [.. (await Walk.MeasureManyAsync(asked, keep, progress, ct)).Select(rewrite)];

        public ValueTask<AgedScan> MeasureAgedAsync(
            string path, MinimumAge age, IProgress<ScanSize>? progress = null, CancellationToken ct = default) =>
            Walk.MeasureAgedAsync(path, age, progress, ct);

        public ValueTask<IReadOnlyList<ScanResult>> MeasureFromDiskAsync(IReadOnlyList<ScanRequest> asked, CancellationToken ct = default) =>
            Walk.MeasureFromDiskAsync(asked, ct);

        public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(DirectorySearch search, string root, CancellationToken ct = default) =>
            Walk.TryFindDirectoriesAsync(search, root, ct);

        public void Invalidate()
        {
        }

        public PassHold Hold() => PassHold.Nothing;
    }
}
