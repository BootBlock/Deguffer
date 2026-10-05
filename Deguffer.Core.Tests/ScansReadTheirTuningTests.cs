using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every scan asks for the values of the drive it reads as it starts, so a setting reaches each of
/// them. The table's read size is seen directly, in the size of the reads the source is handed. A
/// walk's values cannot be seen from outside it, so each walk is shown to ask about its drive, and
/// <c>BoundedFileWalkTests</c> shows the walk runs with the values it is given.
/// </summary>
public sealed class ScansReadTheirTuningTests : IDisposable
{
    private const byte Nvme = 0x11;
    private const int ReadKiB = 8;

    private readonly TempDirectory _temp = new();
    private readonly FakeStorageQueries _queries;
    private readonly ScanTuner _tuner;

    public ScansReadTheirTuningTests()
    {
        var root = Path.GetPathRoot(_temp.Path)!;

        _queries = new FakeStorageQueries().Volume(root, 1).Disk(1, Nvme, seekPenalty: false);
        _tuner = new ScanTuner(
            new FakePreferences(AppPreferences.Default with
            {
                Scanning = ScanPreferences.Default.With(StorageMedia.Nvme, new MediaScanPreferences(TableReadKiB: ReadKiB)),
            }),
            new VolumeMediaCache(_queries),
            new FakeVolumeInventory().With(root));
    }

    public void Dispose() => _temp.Dispose();

    private string Folder()
    {
        _temp.CreateFile(10, "cache", "a.bin");
        return Path.Combine(_temp.Path, "cache");
    }

    [Fact]
    public async Task TheFallbackWalkAsksAboutItsDrive()
    {
        await new DirectoryScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: _tuner)
            .MeasureAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public async Task TheHardLinkWalkAsksAboutItsDrive()
    {
        await new HardLinkAwareScanner(_tuner).MeasureAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public async Task ExplorersWalkAsksAboutItsDrive()
    {
        await new ExploreScanner(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated), tuning: _tuner)
            .ScanAsync(Folder());

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public void TheChromiumWalkAsksAboutItsDrive()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        Directory.CreateDirectory(environment.RoamingAppData);
        Directory.CreateDirectory(environment.LocalAppData);

        new ChromiumUserDataDiscovery(environment, _tuner).Discover();

        Assert.Equal(1, _queries.ExtentsAsked);
    }

    [Fact]
    public async Task TheIndexReadsTheTableInTheSizeTheSettingNames()
    {
        var volume = Path.GetPathRoot(_temp.Path)![0];
        var sources = new RecordingSources(volume);

        await new DirectoryScanner(sources, tuning: _tuner).MeasureAsync($@"{volume}:\Users");

        Assert.Equal(ReadKiB * 1024, sources.LargestRead);
    }

    [Fact]
    public async Task ExploreReadsTheTableInTheSizeTheSettingNames()
    {
        var volume = Path.GetPathRoot(_temp.Path)![0];
        var sources = new RecordingSources(volume);

        await new ExploreScanner(sources, tuning: _tuner).ScanAsync($@"{volume}:\");

        Assert.Equal(ReadKiB * 1024, sources.LargestRead);
    }

    /// <summary>One volume serving a table larger than any read, noting the largest read it is asked for.</summary>
    private sealed class RecordingSources(char volume) : IMftSourceFactory
    {
        public int LargestRead { get; private set; }

        public IMftSource? TryOpen(char driveLetter, out FallbackReason reason)
        {
            reason = driveLetter == volume ? FallbackReason.None : FallbackReason.NotNtfsVolume;

            return driveLetter == volume ? new Recording(this, Table().Build()) : null;
        }

        private static MftFixture Table()
        {
            var fixture = new MftFixture().AddDirectory(6, MftRecord.RootRecordNumber, "Users");

            for (uint i = 0; i < 40; i++)
            {
                fixture.AddFile(20 + i, 6, $"file-{i:D2}.bin", allocated: 4096, logical: 4000);
            }

            return fixture;
        }

        private sealed class Recording(RecordingSources owner, IMftSource inner) : IMftSource
        {
            public int BytesPerRecord => inner.BytesPerRecord;

            public long RecordCount => inner.RecordCount;

            public int BytesPerCluster => inner.BytesPerCluster;

            public int ReadBatch(long firstRecord, Span<byte> destination)
            {
                owner.LargestRead = Math.Max(owner.LargestRead, destination.Length);
                return inner.ReadBatch(firstRecord, destination);
            }

            public bool TryReadClusters(long firstCluster, Span<byte> destination) =>
                inner.TryReadClusters(firstCluster, destination);

            public void Dispose() => inner.Dispose();
        }
    }
}
