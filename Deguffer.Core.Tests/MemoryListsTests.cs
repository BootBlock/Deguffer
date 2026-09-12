using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// The memory lists arrive as twenty-two page counts in an undocumented order. These prove which
/// counts become which list, and that lists which Windows did not return, or which do not add up to
/// <c>GetPerformanceInfo</c>'s available memory, are never handed on.
/// </summary>
public sealed class MemoryListsTests
{
    private const long Page = 4096;
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// A different count in every slot, including the bad and repurposed ones that belong to no list,
    /// so a count taken from the wrong slot changes the answer.
    /// </summary>
    [Fact]
    public void EachListIsTakenFromItsOwnCounts()
    {
        nuint[] counts =
        [
            10, 20, 30, 40, 50,
            100, 200, 300, 400, 500, 600, 700, 800,
            9_001, 9_002, 9_003, 9_004, 9_005, 9_006, 9_007, 9_008,
            60,
        ];

        Assert.Equal(
            new MemoryLists(Zeroed: 10 * Page, Free: 20 * Page, Modified: 70 * Page, Standby: 3_600 * Page),
            MemoryLists.FromPageCounts(counts, Page));
    }

    [Fact]
    public void AnythingButTheWholeStructureIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryLists.FromPageCounts(new nuint[21], Page));

    [Fact]
    public void ListsThatAddUpToAvailableMemoryAgree() =>
        Assert.True(MemoryListCheck.Agrees(Lists(standby: 5_000 * MiB), available: 6_000 * MiB, physicalTotal: 16_000 * MiB));

    /// <summary>A fiftieth of sixteen gigabytes is 320 MB: 300 apart agrees and 400 does not.</summary>
    [Theory]
    [InlineData(300, true)]
    [InlineData(400, false)]
    public void ALargeMachineIsAllowedAFiftiethOfItsMemory(long apartMiB, bool agrees) =>
        Assert.Equal(
            agrees,
            MemoryListCheck.Agrees(Lists(standby: (5_000 + apartMiB) * MiB), available: 6_000 * MiB, physicalTotal: 16_000 * MiB));

    /// <summary>
    /// A fiftieth of two gigabytes is forty megabytes, under the 64 MB floor, so a small machine is
    /// held to the floor instead: 60 apart agrees and 70 does not.
    /// </summary>
    [Theory]
    [InlineData(60, true)]
    [InlineData(70, false)]
    public void ASmallMachineIsAllowedTheFloor(long apartMiB, bool agrees) =>
        Assert.Equal(
            agrees,
            MemoryListCheck.Agrees(Lists(standby: (500 + apartMiB) * MiB), available: 1_500 * MiB, physicalTotal: 2_000 * MiB));

    /// <summary>
    /// Counts too short to be the structure, which reading them would refuse: so the result proves
    /// they were not read at all.
    /// </summary>
    [Fact]
    public void ListsWindowsDidNotReturnAreNeitherReadNorHandedOn() =>
        Assert.Equal<(MemoryLists?, MemoryListState)>(
            (null, MemoryListState.NotReturned),
            MemoryListCheck.Accept(returned: false, new nuint[3], MiB, available: 6_000 * MiB, physicalTotal: 16_000 * MiB));

    [Fact]
    public void ListsThatDoNotAddUpAreNotHandedOn() =>
        Assert.Equal<(MemoryLists?, MemoryListState)>(
            (null, MemoryListState.Disagrees),
            MemoryListCheck.Accept(returned: true, CountsInMiB(), MiB, available: 9_000 * MiB, physicalTotal: 16_000 * MiB));

    [Fact]
    public void ListsThatAddUpAreHandedOn() =>
        Assert.Equal<(MemoryLists?, MemoryListState)>(
            (new MemoryLists(Zeroed: 200 * MiB, Free: 800 * MiB, Modified: 90 * MiB, Standby: 5_000 * MiB), MemoryListState.Checked),
            MemoryListCheck.Accept(returned: true, CountsInMiB(), MiB, available: 6_000 * MiB, physicalTotal: 16_000 * MiB));

    /// <summary>A thousand megabytes of zeroed and free pages together, beside the standby given.</summary>
    private static MemoryLists Lists(long standby) =>
        new(Zeroed: 200 * MiB, Free: 800 * MiB, Modified: 90 * MiB, Standby: standby);

    /// <summary>The same lists as page counts, with pages a megabyte each: six thousand available.</summary>
    private static nuint[] CountsInMiB() =>
    [
        200, 800, 90, 0, 0,
        5_000, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0,
    ];
}
