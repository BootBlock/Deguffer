using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A volume index is kept for the planning pass that built it and no longer. It costs about 49 bytes
/// a slot of the volume's table, over a hundred megabytes on a system volume, and nothing reads it
/// once the pass has ended, because the next pass drops it before it plans.
///
/// <para>Each test sees an index dropped by the volume being opened again for the next question,
/// which is the only way a scanner has of rebuilding one.</para>
/// </summary>
public sealed class VolumeIndexLifetimeTests
{
    private const string Cache = @"\Users\testuser\.npm-cache";

    private static MftFixture Volume() => new MftFixture()
        .AddDirectory(6, MftRecord.RootRecordNumber, "Users")
        .AddDirectory(7, 6, "testuser")
        .AddDirectory(8, 7, ".npm-cache")
        .AddFile(20, 8, "a.tgz", allocated: 8192, logical: 8000);

    [Fact]
    public async Task AHeldIndexAnswersEveryQuestionOfThePassFromOneBuild()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(letter + @":" + Cache);
            await scanner.MeasureAsync(letter + @":\Users");
        }

        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task TheIndexIsDroppedWhenTheLastHoldEnds()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        using (scanner.Hold())
        {
            await scanner.MeasureAsync(letter + @":" + Cache);
        }

        await scanner.MeasureAsync(letter + @":" + Cache);

        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>
    /// Two passes may share one scanner. The first to end must not take the index from the other, and
    /// ending the same hold twice must not count as a second pass ending.
    /// </summary>
    [Fact]
    public async Task TheIndexIsKeptWhileAnotherHoldStands()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);

        var first = scanner.Hold();
        var second = scanner.Hold();
        await scanner.MeasureAsync(letter + @":" + Cache);

        first.Dispose();
        first.Dispose();
        await scanner.MeasureAsync(letter + @":" + Cache);
        Assert.Equal(1, factory.OpenCount);

        second.Dispose();
        await scanner.MeasureAsync(letter + @":" + Cache);
        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>
    /// The whole route: a provider measures through the scanner during a pass, and once the pass has
    /// ended the scanner keeps no index, though nothing invalidated it.
    /// </summary>
    [Fact]
    public async Task APlanningPassLeavesNoIndexBehind()
    {
        var letter = DriveLetters.Unused();
        var factory = FakeMftSourceFactory.Serving(letter, Volume());
        var scanner = new DirectoryScanner(factory, tuning: RouteTuners.Table);
        using var temp = new TempDirectory();
        var provider = new MeasuringProvider(new FakeUserEnvironment(temp.Path), scanner, letter + @":" + Cache);

        await new CleanupPlanner([provider]).PlanAllAsync();

        Assert.Equal(ScanStrategy.MasterFileTable, provider.Measured?.Strategy);
        Assert.Equal(1, factory.OpenCount);

        await scanner.MeasureAsync(letter + @":" + Cache);

        Assert.Equal(2, factory.OpenCount);
    }

    /// <summary>A provider holds the scanner it measures with, and the pass gives the hold back.</summary>
    [Fact]
    public async Task AProviderHoldsItsScannerForThePass()
    {
        using var temp = new TempDirectory();
        var scanner = new FakeDirectoryScanner();
        var provider = new MeasuringProvider(new FakeUserEnvironment(temp.Path), scanner, temp.CreateDirectory("cache"));

        await new CleanupPlanner([provider]).PlanAllAsync();

        Assert.Equal(1, scanner.HoldsTaken);
        Assert.Equal(0, scanner.HoldsStanding);
    }

    /// <summary>Measures one path twice through its scanner, as several rows of one pass would, and plans nothing.</summary>
    private sealed class MeasuringProvider(IUserEnvironment environment, IDirectoryScanner scanner, string path)
        : CleanupProviderBase(environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, scanner)
    {
        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        public override string Id => "measuring";

        public override string Name => "Measuring";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderDescription Description { get; } = new()
        {
            Category = ProviderCategory.TemporaryFiles,
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Measures one path.",
            Recommendation = "None.",
        };

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        /// <summary>The second reading of the pass.</summary>
        public ScanResult? Measured { get; private set; }

        protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
        {
            await Scanner.MeasureAsync(path, keep, ct: ct);
            Measured = await Scanner.MeasureAsync(path, keep, ct: ct);

            return new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = [],
            };
        }
    }
}
