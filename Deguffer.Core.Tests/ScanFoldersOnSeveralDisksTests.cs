using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="ExploreScanner.ScanFoldersAsync"/> over folders on more than one volume: the volumes on
/// separate disks scanned at the same time, and those on one disk, or on a disk not known, in turn.
///
/// <para>Each table is opened through a <see cref="CompanyMftSourceFactory"/>, which holds every open
/// until another volume's open arrives or its wait passes, and counts the most that were held at once.
/// Two at once is only possible where two pipelines ran together.</para>
/// </summary>
public sealed class ScanFoldersOnSeveralDisksTests
{
    /// <summary>How long an open that must have company waits for it before the test fails (#395).</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an open that must be alone waits for company that would prove it was not. Long enough
    /// for a pipeline started at the same time to reach its open on a busy machine, and paid in full by
    /// each open that is rightly alone.
    /// </summary>
    private static readonly TimeSpan Company = TimeSpan.FromSeconds(1);

    private static MftFixture Photos()
    {
        var first = MftRecord.ReservedRecordCount;

        return new MftFixture()
            .AddDirectory(first, MftRecord.RootRecordNumber, "Photos")
            .AddFile(first + 1, first, "a.jpg", allocated: 4096, logical: 100);
    }

    private static ScanTuner Tuner(FakeStorageQueries storage, params string[] volumes)
    {
        var inventory = new FakeVolumeInventory();

        foreach (var volume in volumes)
        {
            inventory.With(volume);
        }

        return new ScanTuner(
            new FakePreferences(AppPreferences.Default with { Scanning = ScanPreferences.Default with { Route = ScanRoute.Table } }),
            new VolumeMediaCache(storage),
            inventory);
    }

    [Fact]
    public async Task VolumesOnSeparateDisksAreReadAtTheSameTime()
    {
        var sources = new CompanyMftSourceFactory(Deadline, ('X', Photos()), ('Y', Photos()));
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        var scans = await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(2, sources.MostAtOnce);
        Assert.Equal([@"X:\Photos\a.jpg", @"Y:\Photos\a.jpg"], scans.Select(scan => scan.Tree.PathOf(Assert.Single(scan.Tree.ChildrenOf(scan.Node).ToArray()))));
        Assert.All(scans, scan => Assert.Equal(ScanStrategy.MasterFileTable, scan.Strategy));
    }

    /// <summary>Two volumes of one spinning disk read at once would make it seek between them for the length of both.</summary>
    [Fact]
    public async Task VolumesOnOneDiskAreReadInTurn()
    {
        var sources = new CompanyMftSourceFactory(Company, ('X', Photos()), ('Y', Photos()));
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 1), @"X:\", @"Y:\");

        await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(1, sources.MostAtOnce);
        Assert.Equal(2, sources.OpenCount);
    }

    /// <summary>A volume that would not say which disk it is on might be on the disk of any other.</summary>
    [Fact]
    public async Task AVolumeOnADiskNotKnownIsReadInTurnWithTheOthers()
    {
        var sources = new CompanyMftSourceFactory(Company, ('X', Photos()), ('Y', Photos()));
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1), @"X:\", @"Y:\");

        await new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]);

        Assert.Equal(1, sources.MostAtOnce);
    }

    /// <summary>
    /// The search has failed once one pipeline has, so the others are stopped rather than left to read
    /// whole volumes, and what is thrown is the failure, never the stop it caused.
    ///
    /// <para>The other pipeline is listed first, and its read waits on nothing but its token, so the
    /// scan ends only if the failure stops it.</para>
    /// </summary>
    [Fact]
    public async Task OnePipelinesFailureStopsTheOthersAndIsWhatIsThrown()
    {
        var sources = new FailingMftSourceFactory(Photos(), failing: 'Y', held: 'X');
        var tuner = Tuner(new FakeStorageQueries().Volume(@"X:\", 1).Volume(@"Y:\", 2), @"X:\", @"Y:\");

        var scan = new ExploreScanner(sources, tuning: tuner).ScanFoldersAsync([@"X:\Photos", @"Y:\Photos"]).AsTask();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => scan.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(FailingMftSourceFactory.Failure, thrown.Message);
    }
}
