using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// What reading the volume list asks of the machine, and on which thread, through a machine whose
/// answers a test sets: which devices are asked about, and that a window reading the list is never
/// the one that waits for it.
///
/// <para>Apart from <see cref="VolumeInventoryTests"/>, which reads the real machine and can say
/// nothing about a share, because the machine running the suite may have none.</para>
/// </summary>
public sealed class VolumeListingTests
{
    /// <summary>How long a held reading waits before it goes on regardless, so a defect fails a test rather than hanging it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A mapped share is listed and its root is never touched. A share that is not there answers
    /// only once the redirector gives up on it, and the list is read as the Explore page opens, so
    /// the window froze for as long as that took. The local letter beside it is asked, which is what
    /// shows this fake would have seen the share asked too.
    /// </summary>
    [Fact]
    public void ANetworkLetterIsNeverProbed()
    {
        using var machine = new FakeVolumeQueries()
            .Letter(@"C:\", DriveType.Fixed)
            .Letter(@"Z:\", DriveType.Network);

        var volumes = new VolumeInventory(machine).Volumes;

        var share = Assert.Single(volumes, volume => volume.RootPath == @"Z:\");
        Assert.Equal(DriveType.Network, share.Kind);
        Assert.Equal(VolumeReadiness.NotAsked, share.Readiness);
        Assert.Equal(VolumeReadiness.Ready, Assert.Single(volumes, volume => volume.RootPath == @"C:\").Readiness);

        Assert.Equal([@"C:\"], machine.Asked.Distinct());
    }

    /// <summary>
    /// The window's reading is taken on another thread, and the list as it was answers anyone who
    /// reads it meanwhile. Read on the caller's own thread, the call would not come back until the
    /// machine had answered, and that is the freeze.
    /// </summary>
    [Fact]
    public async Task ARefreshReadsTheMachineOffTheCallersThreadAndKeepsTheOldListMeanwhile()
    {
        using var machine = new FakeVolumeQueries().Letter(@"C:\", DriveType.Fixed);
        var inventory = new VolumeInventory(machine);
        var before = inventory.Volumes;

        machine.Hold();
        var refreshing = inventory.RefreshAsync(CancellationToken.None);

        // The reading is held until released below, so a call that came back while the machine
        // had already answered is one that waited for it on this thread.
        Assert.False(machine.HeldReadingAnswered, "The refresh came back only once the machine had answered.");
        Assert.True(machine.WaitUntilHeld(Patience), "The machine was never read.");
        Assert.False(refreshing.IsCompleted);
        Assert.Same(before, inventory.Volumes);

        machine.Letter(@"D:\", DriveType.Fixed);
        machine.Release();

        var after = await refreshing;

        Assert.Equal([@"C:\"], after.Select(volume => volume.RootPath));
        Assert.Same(after, inventory.Volumes);
    }

    /// <summary>
    /// A list read across an invalidation is not kept. The caller that invalidated, a planning pass,
    /// asked for a reading taken after it, so a drive mounted in between has to be in the list.
    /// </summary>
    [Fact]
    public async Task AReadingAcrossAnInvalidationIsTakenAgain()
    {
        using var machine = new FakeVolumeQueries().Letter(@"C:\", DriveType.Fixed);
        var inventory = new VolumeInventory(machine);

        machine.Hold();
        var refreshing = inventory.RefreshAsync(CancellationToken.None);

        Assert.True(machine.WaitUntilHeld(Patience), "The machine was never read.");

        inventory.Invalidate();
        machine.Letter(@"D:\", DriveType.Fixed);
        machine.Release();

        var after = await refreshing;

        Assert.Equal([@"C:\", @"D:\"], after.Select(volume => volume.RootPath));
        Assert.Same(after, inventory.Volumes);
    }

    /// <summary>
    /// A machine of drive letters only, each of a kind the test names, every local root present.
    /// <see cref="Hold"/> makes the next reading stop once it has taken the letters, until
    /// <see cref="Release"/>, so a test can act while a reading is under way.
    /// </summary>
    private sealed class FakeVolumeQueries : IVolumeQueries, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly List<(string Root, DriveType Kind)> _letters = [];
        private readonly List<string> _asked = [];
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _released = new(initialState: true);

        /// <summary>Every root whose device a reading asked about, in order.</summary>
        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_gate)
                {
                    return [.. _asked];
                }
            }
        }

        public FakeVolumeQueries Letter(string root, DriveType kind)
        {
            lock (_gate)
            {
                _letters.Add((root, kind));
            }

            return this;
        }

        /// <summary>Whether a reading made while held has returned its letters, which only <see cref="Release"/> or running out of patience lets it do.</summary>
        public bool HeldReadingAnswered => _answered;

        private volatile bool _answered;

        public void Hold()
        {
            _answered = false;
            _held.Reset();
            _released.Reset();
        }

        public bool WaitUntilHeld(TimeSpan patience) => _held.Wait(patience);

        public void Release() => _released.Set();

        public void Dispose()
        {
            _held.Dispose();
            _released.Dispose();
        }

        public IEnumerable<string> Names() => [];

        public IReadOnlyList<string> MountPointsOf(string volumeName) => [];

        public IReadOnlyList<string> Letters()
        {
            IReadOnlyList<string> letters;

            lock (_gate)
            {
                letters = [.. _letters.Select(letter => letter.Root)];
            }

            // Taken before the hold, so what this reading returns is the machine as it was when
            // the reading began.
            _held.Set();

            var wasHeld = !_released.IsSet;
            _released.Wait(Patience);

            if (wasHeld)
            {
                _answered = true;
            }

            return letters;
        }

        public DriveType KindOf(string mountPoint)
        {
            lock (_gate)
            {
                return _letters.First(letter => letter.Root == mountPoint).Kind;
            }
        }

        public PathPresence Probe(string mountPoint)
        {
            lock (_gate)
            {
                _asked.Add(mountPoint);
            }

            return PathPresence.Present;
        }

        public (string? Label, VolumeFeatures? Features, string? FileSystem) InformationOf(string mountPoint)
        {
            lock (_gate)
            {
                _asked.Add(mountPoint);
            }

            return (null, VolumeFeatures.ReparsePoints, "NTFS");
        }

        public (long Total, long Free)? SpaceOf(string mountPoint)
        {
            lock (_gate)
            {
                _asked.Add(mountPoint);
            }

            return (100, 50);
        }
    }
}
