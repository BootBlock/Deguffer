using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two walking scanners over directories on drives tuned differently (#470): the walks on separate
/// disks run at the same time, the walks where a disk is not known run in turn, and the results come
/// in the order asked.
///
/// <para>Directories on drives tuned alike share one walk, which lists them together within its own
/// bound whatever disks they are on. So these drives are tuned apart: the user gave NVMe a thread count
/// of its own, and the first volume is on an NVMe disk while the second is on a hard disk, or on a disk
/// that would not say.</para>
///
/// <para>Each volume is a folder of a temp directory, which the inventory lists as mounted there and
/// the storage fake places on a disk. Each walk is held in a <see cref="Company"/> as it reads its
/// directory: <see cref="ParallelEnumerationScanner"/> as it lists the root, and
/// <see cref="HardLinkAwareScanner"/> as it reads the file in it.</para>
/// </summary>
public sealed class WalkOnSeveralDisksTests : IDisposable
{
    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    /// <summary>How long a walk that must have company waits for it before the test fails (#395).</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>How long a walk that must be alone waits for company that would prove it was not.</summary>
    private static readonly TimeSpan Alone = TimeSpan.FromSeconds(1);

    private readonly TempDirectory _temp = new();
    private readonly string _first;
    private readonly string _second;

    /// <summary>A second directory on the first volume, so the walks of a disk set come in an order the request does not.</summary>
    private readonly string _alsoFirst;

    public WalkOnSeveralDisksTests()
    {
        _first = Path.GetDirectoryName(_temp.CreateFile(1024, "first", "cache", "one.bin"))!;
        _second = Path.GetDirectoryName(_temp.CreateFile(2048, "second", "cache", "two.bin"))!;
        _alsoFirst = Path.GetDirectoryName(_temp.CreateFile(4096, "first", "other", "three.bin"))!;
    }

    public void Dispose() => _temp.Dispose();

    private string Volume(string name) => Path.Combine(_temp.Path, name) + Path.DirectorySeparatorChar;

    /// <summary>The first volume on an NVMe disk, and the second on a hard disk or, where not <paramref name="secondSaid"/>, on a disk it would not name.</summary>
    private ScanTuner Tuner(bool secondSaid)
    {
        var storage = new FakeStorageQueries()
            .Volume(Volume("first"), 1)
            .Disk(1, Nvme, seekPenalty: false)
            .Disk(2, Sata, seekPenalty: true);

        if (secondSaid)
        {
            storage.Volume(Volume("second"), 2);
        }

        return new ScanTuner(
            new FakePreferences(AppPreferences.Default with
            {
                Scanning = ScanPreferences.Default.With(StorageMedia.Nvme, new MediaScanPreferences(WalkThreads: 3)),
            }),
            new VolumeMediaCache(storage),
            new FakeVolumeInventory().With(Volume("first")).With(Volume("second")));
    }

    private bool IsAsked(string path) =>
        LongPath.Display(path) is var shown
        && (shown.StartsWith(_first, StringComparison.OrdinalIgnoreCase) || shown.StartsWith(_second, StringComparison.OrdinalIgnoreCase));

    private ParallelEnumerationScanner Listing(ScanTuner tuner, Company company, TimeSpan wait) =>
        new(
            tuner,
            (directory, request, into) =>
            {
                if (IsAsked(directory))
                {
                    company.Wait(wait);
                }

                return DirectoryQuery.List(directory, request, into);
            },
            TimeProvider.System);

    private HardLinkAwareScanner Reading(ScanTuner tuner, Company company, TimeSpan wait) =>
        new(
            tuner,
            new FileInformation(
                FileInformation.Open,
                FileInformation.ReadIdentity,
                readByName: (string path, out FileInformation.FileStandardInfo info) =>
                {
                    if (IsAsked(path))
                    {
                        company.Wait(wait);
                    }

                    return StatByName.Read(path, out info);
                }));

    [Fact]
    public async Task TheWalkOfEachDiskRunsAtTheSameTime()
    {
        var company = new Company();

        await Listing(Tuner(secondSaid: true), company, Deadline).MeasureManyAsync([new(_first), new(_second)]);

        Assert.Equal(2, company.MostAtOnce);
    }

    /// <summary>A drive that would not name its disk might be on the other's, so its walk waits its turn.</summary>
    [Fact]
    public async Task AWalkOnADiskNotKnownRunsInTurnWithTheOthers()
    {
        var company = new Company();

        await Listing(Tuner(secondSaid: false), company, Alone).MeasureManyAsync([new(_first), new(_second)]);

        Assert.Equal(1, company.MostAtOnce);
    }

    [Fact]
    public async Task TheWalkResultsComeInTheOrderAsked()
    {
        var results = await Listing(Tuner(secondSaid: true), new Company(), Deadline).MeasureManyAsync([new(_first), new(_second), new(_alsoFirst)]);

        Assert.Equal([1024L, 2048L, 4096L], results.Select(result => result.Size.Logical));
    }

    [Fact]
    public async Task TheHardLinkWalkOfEachDiskRunsAtTheSameTime()
    {
        var company = new Company();

        await Reading(Tuner(secondSaid: true), company, Deadline).MeasureManyAsync([new(_first), new(_second)]);

        Assert.Equal(2, company.MostAtOnce);
    }

    [Fact]
    public async Task AHardLinkWalkOnADiskNotKnownRunsInTurnWithTheOthers()
    {
        var company = new Company();

        await Reading(Tuner(secondSaid: false), company, Alone).MeasureManyAsync([new(_first), new(_second)]);

        Assert.Equal(1, company.MostAtOnce);
    }

    [Fact]
    public async Task TheHardLinkWalkResultsComeInTheOrderAsked()
    {
        var results = await Reading(Tuner(secondSaid: true), new Company(), Deadline).MeasureManyAsync([new(_first), new(_second), new(_alsoFirst)]);

        Assert.Equal([1024L, 2048L, 4096L], results.Select(result => result.Size.Logical));
    }
}
