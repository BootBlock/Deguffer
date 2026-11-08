using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="DirectoryScanner.MeasureManyAsync"/> over paths on more than one volume (#470): the
/// volumes on separate disks measured at the same time, those on one disk in turn, the results in the
/// order asked, and one volume's failure stopping the rest.
///
/// <para>Each volume's table is opened on the thread that measures it, so an open held until another
/// volume's open arrives shows two volumes measured at once. Where two must be, the hold waits for the
/// other with a timeout that fails the test only when the other never starts (#395). Where they must
/// not be, it waits <see cref="Company"/>, so a scanner that ran them at once is caught unless the
/// machine started the second open that much late.</para>
/// </summary>
public sealed class MeasureOnSeveralDisksTests
{
    /// <summary>How long an open that must have company waits for it before the test fails.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>How long an open that must be alone waits for company that would prove it was not.</summary>
    private static readonly TimeSpan Company = TimeSpan.FromSeconds(1);

    private static MftFixture Photos(long allocated)
    {
        var first = MftRecord.ReservedRecordCount;

        return new MftFixture()
            .AddDirectory(first, MftRecord.RootRecordNumber, "Photos")
            .AddFile(first + 1, first, "a.jpg", allocated: allocated, logical: allocated);
    }

    /// <summary>The table waited for, so no path is walked on the machine running the suite.</summary>
    private static ScanTuner Tuner(FakeStorageQueries storage) => new(
        new FakePreferences(AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = ScanRoute.Table } }),
        new VolumeMediaCache(storage),
        new FakeVolumeInventory().With(@"X:\").With(@"Y:\"));

    private static DirectoryScanner Scanner(IMftSourceFactory sources, FakeStorageQueries storage) =>
        new(sources, tuning: Tuner(storage));

    [Fact]
    public async Task VolumesOnSeparateDisksAreMeasuredAtTheSameTime()
    {
        var sources = new CompanyMftSourceFactory(Deadline, ('X', Photos(4096)), ('Y', Photos(8192)));

        await Scanner(sources, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2))
            .MeasureManyAsync([new(@"X:\Photos"), new(@"Y:\Photos")]);

        Assert.Equal(2, sources.MostAtOnce);
    }

    /// <summary>Two volumes of one spinning disk measured at once would make it seek between them for the length of both.</summary>
    [Fact]
    public async Task VolumesOnOneDiskAreMeasuredInTurn()
    {
        var sources = new CompanyMftSourceFactory(Company, ('X', Photos(4096)), ('Y', Photos(8192)));

        await Scanner(sources, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 1))
            .MeasureManyAsync([new(@"X:\Photos"), new(@"Y:\Photos")]);

        Assert.Equal(1, sources.MostAtOnce);
        Assert.Equal(2, sources.OpenCount);
    }

    /// <summary>A volume that would not say which disk it is on might be on the disk of any other.</summary>
    [Fact]
    public async Task AVolumeOnADiskNotKnownIsMeasuredInTurnWithTheOthers()
    {
        var sources = new CompanyMftSourceFactory(Company, ('X', Photos(4096)), ('Y', Photos(8192)));

        await Scanner(sources, new FakeStorageQueries().Volume(@"X:\", 1))
            .MeasureManyAsync([new(@"X:\Photos"), new(@"Y:\Photos")]);

        Assert.Equal(1, sources.MostAtOnce);
    }

    /// <summary>
    /// The volumes are measured in the order of their pipelines, which need not be the order asked,
    /// and each result still lands at the place its path was asked for.
    /// </summary>
    [Fact]
    public async Task ResultsComeInTheOrderAsked()
    {
        var sources = new CompanyMftSourceFactory(Deadline, ('X', Photos(4096)), ('Y', Photos(8192)));

        var results = await Scanner(sources, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2))
            .MeasureManyAsync([new(@"Y:\Photos"), new(@"X:\Photos"), new(@"Y:\Photos")]);

        Assert.Equal([8192L, 4096L, 8192L], results.Select(result => result.Size.Allocated));
        Assert.All(results, result => Assert.Equal(ScanStrategy.MasterFileTable, result.Strategy));
    }

    /// <summary>
    /// The measurement has failed once one volume has, so the others are stopped rather than left
    /// waiting on their tables, and what is thrown is the failure, never the stop it caused.
    ///
    /// <para>The other volume's table read waits on nothing but the build's own token, which only
    /// <see cref="DirectoryScanner.Invalidate"/> cancels, so the measurement ends only if the failure
    /// stops the wait for it.</para>
    /// </summary>
    [Fact]
    public async Task OneVolumesFailureStopsTheOthersAndIsWhatIsThrown()
    {
        var sources = new FailingMftSourceFactory(Photos(4096), failing: 'Y', held: 'X');
        var scanner = Scanner(sources, new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2));

        try
        {
            var measuring = scanner.MeasureManyAsync([new(@"X:\Photos"), new(@"Y:\Photos")]).AsTask();

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => measuring.WaitAsync(Deadline));
            Assert.Equal(FailingMftSourceFactory.Failure, thrown.Message);
        }
        finally
        {
            scanner.Invalidate();
        }
    }
}
