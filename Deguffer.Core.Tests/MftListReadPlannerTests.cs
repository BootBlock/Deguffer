using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which attribute lists kept outside the table are read together: lists in cluster order whose
/// clusters touch or are close, up to the largest a list can be, and anything else alone.
/// </summary>
public sealed class MftListReadPlannerTests
{
    private const int ClusterBytes = 4096;

    /// <summary>How many clusters apart two lists can be and still be read together.</summary>
    private const int Gap = MftReadGap.Bytes / ClusterBytes;

    [Fact]
    public void ReadsListsWhoseClustersTouchAsOne()
    {
        var plan = Plan(OneCluster(700), OneCluster(701), OneCluster(702), OneCluster(900));

        Assert.Equal([new MftListRead(0, 3, 700, 3 * ClusterBytes), new MftListRead(3, 1, 900, ClusterBytes)], plan);
    }

    /// <summary>A list two clusters long ends a cluster later, so the next touches it there.</summary>
    [Fact]
    public void MeasuresWhereAListEndsByItsLength()
    {
        var plan = Plan(([new DataRun(700, 4)], ClusterBytes + 1), OneCluster(702), OneCluster(704 + Gap + 1));

        Assert.Equal(
            [new MftListRead(0, 2, 700, 3 * ClusterBytes), new MftListRead(2, 1, 705 + Gap, ClusterBytes)],
            plan);
    }

    /// <summary>
    /// A gap as long as <see cref="MftReadGap"/> is read across, and a cluster longer ends the read,
    /// as the passes over the table end theirs.
    /// </summary>
    [Fact]
    public void ReadsAcrossAGapNoLongerThanTheReadGap()
    {
        var plan = Plan(OneCluster(700), OneCluster(701 + Gap), OneCluster(702 + Gap + Gap + 1));

        Assert.Equal(
            [new MftListRead(0, 2, 700, (Gap + 2) * ClusterBytes), new MftListRead(2, 1, 702 + Gap + Gap + 1, ClusterBytes)],
            plan);
    }

    /// <summary>A read of several is never larger than the largest list, so its buffer is no larger than one list could ask for.</summary>
    [Fact]
    public void EndsAReadAtTheLargestAListCanBe()
    {
        const int most = MftAttributeList.MaximumLength / ClusterBytes;
        var lists = Enumerable.Range(0, most + 1).Select(i => OneCluster(700 + i)).ToArray();

        var plan = Plan(lists);

        Assert.Equal(
            [new MftListRead(0, most, 700, MftAttributeList.MaximumLength), new MftListRead(most, 1, 700 + most, ClusterBytes)],
            plan);
    }

    /// <summary>
    /// A list whose clusters are not all in its first run cannot be a slice of one read, and a sparse
    /// one has no clusters at all, so each is read alone by its own runs and ends any read beside it.
    /// </summary>
    [Fact]
    public void ReadsAListNotInOnePieceAlone()
    {
        var plan = Plan(
            OneCluster(700),
            ([new DataRun(701, 1), new DataRun(5_000, 1)], 2 * ClusterBytes),
            OneCluster(702),
            ([new DataRun(0, 1, IsSparse: true)], ClusterBytes),
            OneCluster(703));

        Assert.Equal(
            [
                new MftListRead(0, 1, 700, ClusterBytes),
                new MftListRead(1, 1, 701, 2 * ClusterBytes),
                new MftListRead(2, 1, 702, ClusterBytes),
                new MftListRead(3, 1, 0, ClusterBytes),
                new MftListRead(4, 1, 703, ClusterBytes),
            ],
            plan);
    }

    /// <summary>
    /// Two lists sharing clusters is damage, and each is still read whole: the read reaches the end
    /// of whichever ends later.
    /// </summary>
    [Fact]
    public void CoversBothOfTwoListsSharingClusters()
    {
        var plan = Plan(([new DataRun(700, 3)], 3 * ClusterBytes), OneCluster(700), OneCluster(701));

        Assert.Equal([new MftListRead(0, 3, 700, 3 * ClusterBytes)], plan);
    }

    /// <summary>
    /// A start near the largest cluster number comes off a damaged disk, and adding a length to it
    /// wraps negative. Read with the list before it, the wrapped end would fall short of the list's
    /// own cluster, and its slice would lie past the end of the read.
    /// </summary>
    [Fact]
    public void ReadsAListAtTheLargestClusterNumberAlone()
    {
        var plan = Plan(OneCluster(long.MaxValue - 1), OneCluster(long.MaxValue));

        Assert.Equal(
            [new MftListRead(0, 1, long.MaxValue - 1, ClusterBytes), new MftListRead(1, 1, long.MaxValue, ClusterBytes)],
            plan);
    }

    /// <summary>
    /// Where a cluster is larger than the read gap, no gap is read across, and lists are read
    /// together only where their clusters touch.
    /// </summary>
    [Fact]
    public void ReadsAcrossNoGapWhereAClusterIsLargerThanTheReadGap()
    {
        const int large = 2 * MftReadGap.Bytes;

        var plan = MftListReadPlanner.Plan([([new DataRun(10, 1)], large), ([new DataRun(11, 1)], large), ([new DataRun(13, 1)], large)], large);

        Assert.Equal([new MftListRead(0, 2, 10, 2 * large), new MftListRead(2, 1, 13, large)], plan);
    }

    private static (IReadOnlyList<DataRun> Runs, int Length) OneCluster(long cluster) => ([new DataRun(cluster, 1)], 0x100);

    private static IReadOnlyList<MftListRead> Plan(params (IReadOnlyList<DataRun> Runs, int Length)[] lists) =>
        MftListReadPlanner.Plan(lists, ClusterBytes);
}
