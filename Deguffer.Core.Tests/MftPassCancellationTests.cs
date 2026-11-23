using System.Collections.Frozen;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// A pass over a volume's table from a folder near its root steps through millions of nodes, so a
/// scan or preview the user cancels must stop it part-way rather than wait for an answer nobody will
/// read. The check is paid once a stride of nodes, so each tree here is a few strides deep, and a
/// run that is not cancelled must total exactly what it did before the check was added.
/// </summary>
public sealed class MftPassCancellationTests
{
    private const uint Cache = 16;
    private const int Files = 200_000;
    private const long Allocated = 4096;
    private const long Logical = 100;

    private static readonly string[] CachePath = ["cache"];

    /// <summary>
    /// The volume root holding one folder of <see cref="Files"/> files, built straight into the tree
    /// rather than as records to parse, because a table of this size as records is hundreds of
    /// megabytes.
    /// </summary>
    private static MftVolumeIndex FlatCache()
    {
        var count = (int)Cache + 1 + Files;
        var tree = new MftVolumeTree(count);

        tree.Set(MftRecord.RootRecordNumber, Directory(MftRecord.RootRecordNumber), ".", MftOtherLinks.None);
        tree.Set(Cache, Directory(MftRecord.RootRecordNumber), "cache", MftOtherLinks.None);

        for (var file = Cache + 1; file < count; file++)
        {
            tree.Set(file, new MftRecord(Cache, new ScanSize(Allocated, Logical), IsDirectory: false, IsReparsePoint: false, 0, 0), "f.bin", MftOtherLinks.None);
        }

        // The root's one child is the cache, and the cache's children are every record after it.
        var start = new int[count + 1];
        var children = new uint[1 + Files];
        children[0] = Cache;

        for (var i = 0; i < Files; i++)
        {
            children[i + 1] = Cache + 1 + (uint)i;
        }

        for (var record = 0; record <= count; record++)
        {
            start[record] = record <= MftRecord.RootRecordNumber ? 0 : record <= Cache ? 1 : 1 + Files;
        }

        return new MftVolumeIndex(tree, new MftChildLinks(start, children));

        static MftRecord Directory(uint parent) => new(parent, ScanSize.Zero, IsDirectory: true, IsReparsePoint: false, 0, 0);
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    [Fact]
    public void ACancelledMeasurementStopsInsteadOfTotallingTheSubtree()
    {
        var index = FlatCache();

        Assert.Throws<OperationCanceledException>(() =>
            index.TryMeasure(CachePath, [MinimumAge.Off], keepRoot: false, MftVolumeIndex.NothingLeftOut, Cancelled()));
    }

    /// <summary>
    /// Across every stride the check falls on, a measurement nobody cancels counts every file and the
    /// folder holding them.
    /// </summary>
    [Fact]
    public void AnUncancelledMeasurementTotalsEveryFile()
    {
        var measured = FlatCache().TryMeasure(CachePath, [MinimumAge.Off], keepRoot: false, MftVolumeIndex.NothingLeftOut, CancellationToken.None);

        Assert.NotNull(measured);
        Assert.Equal(new ScanSize(Files * Allocated, Files * Logical, Entries: Files + 1), measured.Readings[0].Size);
    }

    [Fact]
    public void ACancelledDirectorySearchStopsInsteadOfFinishingTheSubtree()
    {
        var search = new DirectorySearch(FrozenSet.ToFrozenSet(["node_modules"]), FrozenSet<string>.Empty);

        Assert.Throws<OperationCanceledException>(() => FlatCache().TryFindDirectories(CachePath, search, Cancelled()));
    }
}
