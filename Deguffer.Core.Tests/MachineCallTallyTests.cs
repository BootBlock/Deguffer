using Deguffer.Core.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Tests;

/// <summary>
/// A planning pass counts the dear calls it makes of Windows and the time they take, so where a pass
/// spends its time is measured rather than estimated from the code (#516). Each call is counted where
/// Core hands it to the machine, so these stand in for Windows through the same seams the product
/// reaches it by, and a pass's tally is its own however many run at once.
/// </summary>
public sealed class MachineCallTallyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>What each stand-in for Windows takes, on the manual clock.</summary>
    private static readonly TimeSpan CallTakes = TimeSpan.FromMilliseconds(3);

    private readonly ManualTimeProvider _clock = new();

    /// <summary>
    /// Two providers that each follow three paths their folders cannot place make six calls to
    /// <c>GetVolumePathNameW</c>, and the pass's tally says so, with the time each took.
    /// </summary>
    [Fact]
    public async Task APassOverTwoProvidersThatEachFollowThreePathsCountsSixVolumePathCalls()
    {
        var windows = UnprovableMountPoints();
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            await Planner(
                new FakeCleanupProvider("first") { WhilePlanning = () => Follow(windows, "first") },
                new FakeCleanupProvider("second") { WhilePlanning = () => Follow(windows, "second") })
                .PlanAllAsync()
                .WaitAsync(Deadline);
        }

        var counted = tally.Of(MachineCall.VolumePathName);

        // At least each call's own time: the providers plan side by side on one clock, so a call
        // under way while the other provider's call advances it counts that time too.
        Assert.Equal(6, counted.Calls);
        Assert.True(counted.Elapsed >= CallTakes * 6, $"{counted.Elapsed} is less than the six calls took.");
    }

    /// <summary>
    /// A path whose folders prove its volume costs no call to Windows, so it is not counted: the tally
    /// counts what reached the machine, not what was asked of the lookup.
    /// </summary>
    [Fact]
    public void APathItsFoldersPlaceCountsNoVolumePathCall()
    {
        var lookup = new MountPointLookup(PlainFolder, _ => @"\Device\HarddiskVolume3", _ => @"C:\");
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            Assert.Equal(@"C:\", lookup.MountPointOf(@"C:\Users\testuser\source\app"));
        }

        Assert.Equal(default, tally.Of(MachineCall.VolumePathName));
    }

    /// <summary>
    /// A pass with no live-tree candidates opens no Restart Manager session and reads no process
    /// table, which is what tells a fix that removed the candidates from one that only made each
    /// session cheaper.
    /// </summary>
    [Fact]
    public async Task APassWithNoLiveTreeCandidatesCountsNoRestartManagerSession()
    {
        var holders = new FakeFileHolders();
        var inspector = Inspector(holders);
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            await Planner(new FakeCleanupProvider("row") { WhilePlanning = () => inspector.FindLive([]) })
                .PlanAllAsync()
                .WaitAsync(Deadline);
        }

        Assert.Equal(0, tally.Of(MachineCall.RestartManagerSession).Calls);
        Assert.Equal(0, tally.Of(MachineCall.ProcessTable).Calls);
        Assert.Empty(holders.Sessions);
    }

    /// <summary>
    /// Candidates with lock files are asked about in a session, counted once for each session opened,
    /// and the process table read to match them is counted once. One held candidate among three costs
    /// the shared session and one session each.
    /// </summary>
    [Fact]
    public async Task APassCountsEachRestartManagerSessionAndTheProcessTableReading()
    {
        var holders = new FakeFileHolders();
        var queries = Candidates(3);
        holders.Holding(queries[1].LockFileNames[0], "devenv");
        var inspector = Inspector(holders);
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            await Planner(new FakeCleanupProvider("row") { WhilePlanning = () => inspector.FindLive(queries) })
                .PlanAllAsync()
                .WaitAsync(Deadline);
        }

        Assert.Equal(4, holders.Sessions.Count);
        Assert.Equal(4, tally.Of(MachineCall.RestartManagerSession).Calls);
        Assert.Equal(1, tally.Of(MachineCall.ProcessTable).Calls);
    }

    /// <summary>
    /// Two passes planned at the same time, each in its own tally, count only their own calls: the
    /// planner runs providers side by side, and so do the tests.
    /// </summary>
    [Fact]
    public async Task PassesPlannedAtTheSameTimeEachCountOnlyTheirOwnCalls()
    {
        var windows = UnprovableMountPoints();
        using var bothPlanning = new Barrier(2);
        var first = new MachineCallTally(_clock);
        var second = new MachineCallTally(_clock);

        void Meet(string name, int paths)
        {
            Assert.True(bothPlanning.SignalAndWait(Deadline), "The two passes did not overlap.");
            Follow(windows, name, paths);
        }

        await Task.WhenAll(
            Plan(first, () => Meet("first", paths: 2)),
            Plan(second, () => Meet("second", paths: 5)))
            .WaitAsync(Deadline * 2);

        Assert.Equal(2, first.Of(MachineCall.VolumePathName).Calls);
        Assert.Equal(5, second.Of(MachineCall.VolumePathName).Calls);

        async Task Plan(MachineCallTally tally, Action whilePlanning)
        {
            await Task.Yield();

            using (tally.CountIn())
            {
                await Planner(new FakeCleanupProvider("row") { WhilePlanning = whilePlanning }).PlanAllAsync();
            }
        }
    }

    /// <summary>
    /// A tally counts only while its scope is open, and closing an inner one gives the flow back to
    /// the outer one rather than to none.
    /// </summary>
    [Fact]
    public void ATallyCountsOnlyWhileItsScopeIsOpenAndAnInnerOneGivesTheFlowBack()
    {
        var windows = UnprovableMountPoints();
        var outer = new MachineCallTally(_clock);
        var inner = new MachineCallTally(_clock);

        using (outer.CountIn())
        {
            Follow(windows, "outer", paths: 1);

            using (inner.CountIn())
            {
                Follow(windows, "inner", paths: 2);
            }

            Follow(windows, "outer again", paths: 1);
        }

        Follow(windows, "after", paths: 4);

        Assert.Equal(2, outer.Of(MachineCall.VolumePathName).Calls);
        Assert.Equal(2, inner.Of(MachineCall.VolumePathName).Calls);
    }

    /// <summary>
    /// <see cref="IVolumeInventory.MountPointsOf"/> is two mount-manager calls, and reading the list
    /// of volumes asks the second once for each volume. Each is counted with its own time.
    /// </summary>
    [Fact]
    public void TheInventoryCountsBothMountManagerCalls()
    {
        IVolumeInventory inventory = new VolumeInventory(new TimedQueries(_clock));
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            Assert.Equal([@"D:\", @"E:\Mount\"], inventory.MountPointsOf(@"D:\"));
            Assert.Equal([@"D:\", @"E:\Mount\"], inventory.MountPointsOf(@"E:\Mount"));
            _ = inventory.Volumes;
        }

        Assert.Equal(new MachineCallCount(2, CallTakes * 2), tally.Of(MachineCall.VolumeNameOfMountPoint));
        Assert.Equal(new MachineCallCount(3, CallTakes * 3), tally.Of(MachineCall.MountPointsOfVolume));
    }

    /// <summary>Every read of where a handle is open on is counted, the one behind a description included.</summary>
    [Fact]
    public void EveryFinalPathReadIsCounted()
    {
        using var handle = new SafeFileHandle(1, ownsHandle: false);
        var files = new FileInformation(
            (_, _) => handle,
            (SafeFileHandle _, IdentityRoute _, out FileIdentity identity) =>
            {
                identity = default;
                return false;
            },
            finalPath: _ =>
            {
                _clock.Advance(CallTakes);
                return @"\\?\C:\Users\testuser\file.txt";
            });
        var tally = new MachineCallTally(_clock);

        using (tally.CountIn())
        {
            files.FinalPath(@"C:\Users\testuser\file.txt");
            files.FinalPath(@"C:\Users\testuser\other.txt");
        }

        Assert.Equal(new MachineCallCount(2, CallTakes * 2), tally.Of(MachineCall.FinalPathName));
    }

    private static CleanupPlanner Planner(params ICleanupProvider[] providers) =>
        new(providers, volumes: new FakeVolumeInventory());

    /// <summary>A lookup whose folders never prove a volume, so every path is asked of the stand-in for Windows.</summary>
    private MountPointLookup UnprovableMountPoints() =>
        new(PlainFolder, _ => null, _ =>
        {
            _clock.Advance(CallTakes);
            return @"C:\";
        });

    private static int PlainFolder(string extendedPath, out FileAttributes attributes)
    {
        attributes = FileAttributes.Directory;
        return 0;
    }

    private static void Follow(MountPointLookup lookup, string row, int paths = 3)
    {
        for (var i = 0; i < paths; i++)
        {
            lookup.MountPointOf($@"C:\Users\testuser\{row}\path{i}");
        }
    }

    private static LiveTreeInspector Inspector(FakeFileHolders holders) =>
        new(new FakeProcessTableCalls().With(FakeProcessTableCalls.Own), new FakeVolumeInventory(), holdersOf: holders.Query);

    private static List<LiveTreeQuery> Candidates(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i =>
        {
            var solution = $@"C:\Users\testuser\src\solution{i}";
            var vs = Path.Combine(solution, ".vs");

            return new LiveTreeQuery(vs, solution, [Path.Combine(vs, "index.vsidx"), Path.Combine(vs, "Browse.VC.db")])
            {
                LockFilesListed = true,
            };
        }),
    ];

    /// <summary>
    /// One volume, mounted at <c>D:\</c> and at <c>E:\Mount\</c>, each mount-manager call taking
    /// <see cref="CallTakes"/> on the manual clock. Nothing else about the machine is asked.
    /// </summary>
    private sealed class TimedQueries(ManualTimeProvider clock) : IVolumeQueries
    {
        private const string Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";

        public IEnumerable<string> Names() => [Volume];

        public string? VolumeNameOf(string mountPoint)
        {
            clock.Advance(CallTakes);
            return mountPoint is @"D:\" or @"E:\Mount\" ? Volume : null;
        }

        public IReadOnlyList<string> MountPointsOf(string volumeName)
        {
            clock.Advance(CallTakes);
            return volumeName == Volume ? [@"D:\", @"E:\Mount\"] : [];
        }

        public IReadOnlyList<string> Letters() => [];

        public DriveType KindOf(string mountPoint) => DriveType.Fixed;

        public PathPresence Probe(string mountPoint) => PathPresence.Present;

        public (string? Label, VolumeFeatures? Features, string? FileSystem) InformationOf(string mountPoint) => (null, null, null);

        public (long Total, long Free)? SpaceOf(string mountPoint) => null;
    }
}
