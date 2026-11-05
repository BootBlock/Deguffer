using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a drive is mounted is remembered by the owners that already remember each path's answer, and
/// dropped when they drop theirs (#513): one reading of the process table, and one provider's pass.
/// A volume mounted at a folder between two of them is seen by the second.
/// </summary>
public sealed class RememberedMountPointsOwnerTests : IDisposable
{
    private const string Source = @"C:\Users\testuser\src";

    private readonly TempDirectory _temp = new();
    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A hundred candidates on one drive ask where it is mounted once for a reading of the process
    /// table. Once that reading is discarded, the next asks again, and a program running from a build
    /// output through a mount of the drive made in between makes that output live (§5.6).
    /// </summary>
    [Fact]
    public void AReadingOfTheProcessTableAsksWhereADriveIsMountedOnceAndTheNextAsksAgain()
    {
        var clock = new ManualTimeProvider();
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess { Id = 4400, Name = "tool", ImagePath = @"C:\Tools\tool.exe" });
        var inspector = new LiveTreeInspector(calls, _volumes, clock);
        IReadOnlyList<LiveTreeQuery> candidates =
        [
            .. Enumerable.Range(0, 100).Select(index => new LiveTreeQuery($@"{Source}\project{index}\bin", $@"{Source}\project{index}")),
        ];

        Assert.False(inspector.FindLive(candidates).IsLive(candidates[0].Directory));
        inspector.FindLive(candidates);

        Assert.Equal([@"C:\"], _volumes.MountPointsQueries);

        _volumes.Without(@"C:\").With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);
        calls.With(new FakeListedProcess
        {
            Id = 4401,
            Name = "app",
            ImagePath = @"Q:\SysMount\Users\testuser\src\project0\bin\app.exe",
        });
        clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromSeconds(1));
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);

        var findings = inspector.FindLive(candidates);

        Assert.True(findings.IsLive(candidates[0].Directory));
        Assert.False(findings.IsLive(candidates[1].Directory));
        Assert.Equal(2, _volumes.MountPointsQueries.Count(asked => asked == @"C:\"));
        Assert.Contains(@"Q:\SysMount\Users\testuser\src\project1", inspector.Reach($@"{Source}\project1").Places);
    }

    /// <summary>
    /// A provider asks where a drive is mounted once for a pass, and the next pass asks again, so a
    /// folder it follows then is reachable through a mount of the drive made in between.
    /// </summary>
    [Fact]
    public void AProviderAsksWhereADriveIsMountedOncePerPass()
    {
        var provider = new ReachingProvider(new FakeUserEnvironment(_temp.Path), _volumes);

        provider.Reached($@"{Source}\first");
        provider.Reached($@"{Source}\second");

        Assert.Equal([@"C:\"], _volumes.MountPointsQueries);

        _volumes.Without(@"C:\").With(@"C:\", alsoMountedAt: [@"Q:\SysMount\"]);
        provider.InvalidateCaches();

        Assert.Contains(@"Q:\SysMount\Users\testuser\src\third", provider.Reached($@"{Source}\third").Places);
        Assert.Equal(2, _volumes.MountPointsQueries.Count);
    }

    /// <summary>A provider that plans nothing and follows the folders it is asked about.</summary>
    private sealed class ReachingProvider(IUserEnvironment environment, IVolumeInventory volumes)
        : CleanupProviderBase(
            environment,
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            new FakeDirectoryScanner(),
            volumes: volumes)
    {
        public override string Id => "reaching";

        public override string Name => "Reaching";

        public override SafetyTier Tier => SafetyTier.RegenerableCache;

        public override StepGrain Grain => StepGrain.Parts;

        public override string WhatHappensOnNextUse => "Nothing a test cares about.";

        public override ProviderBasis Basis => ProviderBasis.Observed;

        public override ProviderDescription Description { get; } = new()
        {
            Category = ProviderCategory.TemporaryFiles,
            Application = "a test",
            Publisher = "the suite",
            Purpose = "Follows the folders it is asked about.",
            Recommendation = "None.",
        };

        public ReachedFolder Reached(string path) => Reach(path);

        public override Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(true);

        public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        protected override Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct) =>
            Task.FromResult(new CleanupPlan
            {
                ProviderId = Id,
                ProviderName = Name,
                Tier = Tier,
                WhatHappensOnNextUse = WhatHappensOnNextUse,
                Steps = [],
            });
    }
}
