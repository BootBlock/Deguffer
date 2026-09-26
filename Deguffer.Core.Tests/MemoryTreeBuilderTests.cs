using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The memory tree is the picture §7.2 describes: Applications by process tree, Services by host, and
/// Windows with the memory no figure attributes drawn and labelled. These prove where each process
/// goes, that a parent counts only where the creation times allow it, that no page is drawn twice, and
/// that the remainder is never less than nothing. Every name and figure is invented.
/// </summary>
public sealed class MemoryTreeBuilderTests
{
    private const long MiB = MemorySnapshotBuilder.MiB;

    [Fact]
    public void TheRootIsPhysicalMemoryAndHoldsTheThreeParts()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 500, created: 10).Build());

        Assert.Equal(16_000 * MiB, tree.SizeOf(tree.RootNode));
        Assert.Equal(0, tree.Overcount);

        // The walk that colours a branch climbs to the root and stops there, so the root has to answer
        // for itself rather than for a node above it.
        Assert.Equal(tree.RootNode, tree.ParentOf(tree.RootNode));
        Assert.Equal(
            [MemoryPart.Applications, MemoryPart.Services, MemoryPart.Windows],
            PartsUnder(tree, tree.RootNode).Order());
    }

    [Fact]
    public void AParentCreatedBeforeItsChildHoldsIt()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Build());

        var parent = Node(tree, MemoryPart.Process, 100, 10);

        Assert.Equal(parent, tree.ParentOf(Node(tree, MemoryPart.Process, 200, 20)));
        Assert.Equal(Node(tree, MemoryPart.Applications), tree.ParentOf(parent));
        Assert.Equal(400 * MiB, tree.SizeOf(parent));
    }

    /// <summary>A child created at the very tick its parent was is still its child: only a later parent is refused.</summary>
    [Fact]
    public void AParentCreatedAtTheSameMomentStillHoldsItsChild()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 10)
            .Build());

        Assert.Equal(Node(tree, MemoryPart.Process, 100, 10), tree.ParentOf(Node(tree, MemoryPart.Process, 200, 10)));
    }

    /// <summary>
    /// The identifier the child recorded now belongs to a process created after it, so that process
    /// cannot have started it.
    /// </summary>
    [Fact]
    public void AParentCreatedAfterItsChildIsNotItsParent()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 30)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Build());

        Assert.Equal(Node(tree, MemoryPart.Applications), tree.ParentOf(Node(tree, MemoryPart.Process, 200, 20)));
        Assert.Null(tree.Find(new MemoryNodeKey(MemoryPart.OwnShare, 100, 30)));
    }

    [Fact]
    public void AProcessWhoseParentHasExitedSitsAtTheTop()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(200, 999, "beta.exe", 100, created: 20).Build());

        Assert.Equal(Node(tree, MemoryPart.Applications), tree.ParentOf(Node(tree, MemoryPart.Process, 200, 20)));
    }

    /// <summary>
    /// Its own 300 MB beside its child's 100, so the node holding both totals 400 and the share of its
    /// own is a child like any other.
    /// </summary>
    [Fact]
    public void AProcessWithChildrenDrawsItsOwnShareAsAChild()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Build());

        var parent = Node(tree, MemoryPart.Process, 100, 10);
        var own = Node(tree, MemoryPart.OwnShare, 100, 10);

        Assert.Equal(parent, tree.ParentOf(own));
        Assert.Equal(300 * MiB, tree.SizeOf(own));
        Assert.Equal(2, tree.ChildrenOf(parent).Length);
        Assert.False(tree.IsContainer(own));
        Assert.True(tree.IsContainer(parent));
    }

    /// <summary>
    /// The host's parent is an ordinary older process, and the host still sits at the top of Services,
    /// naming what it holds. What the host started is an application: Windows starts packaged
    /// applications and brokers from a service host, and drawing those under it would size them as
    /// memory a service holds.
    /// </summary>
    [Fact]
    public void AServiceHostSitsAtTheTopOfServicesAndWhatItStartedIsAnApplication()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(300, 100, "beta.exe", 200, created: 20)
            .Process(400, 300, "gamma.exe", 50, created: 30)
            .Service("ExampleIndexer", host: 300)
            .Build());

        var host = Node(tree, MemoryPart.Process, 300, 20);

        Assert.Equal(Node(tree, MemoryPart.Services), tree.ParentOf(host));
        Assert.Equal(["ExampleIndexer"], tree.ServicesOf(host).Select(service => service.Name));
        Assert.Equal(200 * MiB, tree.SizeOf(host));
        Assert.Equal(Node(tree, MemoryPart.Applications), tree.ParentOf(Node(tree, MemoryPart.Process, 400, 30)));
        Assert.Null(tree.Find(new MemoryNodeKey(MemoryPart.OwnShare, 100, 10)));
    }

    [Fact]
    public void AHostNamesEveryServiceItHolds()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(300, 1, "beta.exe", 200, created: 20)
            .Service("ExampleIndexer", host: 300)
            .Service("ExampleUpdater", host: 300)
            .Build());

        Assert.Equal(
            ["ExampleIndexer", "ExampleUpdater"],
            tree.ServicesOf(Node(tree, MemoryPart.Process, 300, 20)).Select(service => service.Name).Order());
    }

    [Fact]
    public void TheCompressionStoreIsPartOfWindows()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(80, MemoryTreeBuilder.SystemProcessId, MemoryTreeBuilder.CompressionStoreName, 900, created: 5)
            .Build());

        var store = Node(tree, MemoryPart.CompressionStore);

        Assert.Equal(Node(tree, MemoryPart.Windows), tree.ParentOf(store));
        Assert.Equal(900 * MiB, tree.SizeOf(store));
        Assert.Null(tree.Find(new MemoryNodeKey(MemoryPart.Process, 80, 5)));
    }

    /// <summary>The name alone is anybody's to take, so a process started by something else keeps its place.</summary>
    [Fact]
    public void AProcessNamedLikeTheCompressionStoreButStartedElsewhereIsAnApplication()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(80, 999, MemoryTreeBuilder.CompressionStoreName, 900, created: 5)
            .Build());

        Assert.Equal(Node(tree, MemoryPart.Applications), tree.ParentOf(Node(tree, MemoryPart.Process, 80, 5)));
        Assert.Null(tree.Find(MemoryNodeKey.Of(MemoryPart.CompressionStore)));
    }

    /// <summary>The system cache overlaps the standby list, so where the lists are drawn it is not.</summary>
    [Fact]
    public void CheckedListsAreDrawnInPlaceOfTheSystemCache()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Lists(zeroedMiB: 300, freeMiB: 500, modifiedMiB: 100, standbyMiB: 4_000)
            .Build());

        Assert.Equal(4_000 * MiB, tree.SizeOf(Node(tree, MemoryPart.Standby)));
        Assert.Equal(100 * MiB, tree.SizeOf(Node(tree, MemoryPart.Modified)));
        Assert.Equal(800 * MiB, tree.SizeOf(Node(tree, MemoryPart.Free)));
        Assert.Null(tree.Find(MemoryNodeKey.Of(MemoryPart.SystemCache)));
    }

    [Fact]
    public void WithoutListsTheSystemCacheIsDrawnAndNoListIs()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Build());

        Assert.Equal(3_000 * MiB, tree.SizeOf(Node(tree, MemoryPart.SystemCache)));
        Assert.Null(tree.Find(MemoryNodeKey.Of(MemoryPart.Standby)));
        Assert.Null(tree.Find(MemoryNodeKey.Of(MemoryPart.Modified)));
        Assert.Null(tree.Find(MemoryNodeKey.Of(MemoryPart.Free)));
    }

    /// <summary>16,000 less the cache's 3,000, the pool's 200, two processes' 800 and the store's 900.</summary>
    [Fact]
    public void TheRemainderIsWhatNoOtherPartAccountsFor()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Process(200, 1, "beta.exe", 300, created: 20)
            .Process(80, MemoryTreeBuilder.SystemProcessId, MemoryTreeBuilder.CompressionStoreName, 900, created: 5)
            .Build());

        Assert.Equal(11_100 * MiB, tree.SizeOf(Node(tree, MemoryPart.Unattributed)));
        Assert.Equal(16_000 * MiB, tree.SizeOf(tree.RootNode));
    }

    /// <summary>
    /// A thousand megabytes of memory and 1,200 of parts: the remainder is drawn as nothing, and the 200
    /// the parts overran by is stated instead of hidden.
    /// </summary>
    [Fact]
    public void PartsAddingUpToMoreThanPhysicalMemoryAreStatedRatherThanDrawnBelowZero()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .System(physicalMiB: 1_000, systemCacheMiB: 800, nonPagedPoolMiB: 100)
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Build());

        Assert.Equal(0, tree.SizeOf(Node(tree, MemoryPart.Unattributed)));
        Assert.Equal(200 * MiB, tree.Overcount);
    }

    [Theory]
    [InlineData(ProcessFigures.NothingToCheckAgainst)]
    [InlineData(ProcessFigures.CreationTimeDisagrees)]
    [InlineData(ProcessFigures.NotOnThisArchitecture)]
    public void FiguresTurnedOffDrawNoProcess(ProcessFigures figures)
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Process(80, MemoryTreeBuilder.SystemProcessId, MemoryTreeBuilder.CompressionStoreName, 900, created: 5)
            .Service("ExampleIndexer", host: 100)
            .Figures(figures)
            .Build());

        Assert.DoesNotContain(
            Enumerable.Range(0, tree.NodeCount),
            node => tree.PartOf(node) is MemoryPart.Process or MemoryPart.OwnShare or MemoryPart.CompressionStore);
        Assert.Equal(0, tree.SizeOf(Node(tree, MemoryPart.Applications)));
        Assert.Equal(12_800 * MiB, tree.SizeOf(Node(tree, MemoryPart.Unattributed)));
    }

    [Fact]
    public void TheIdleProcessIsNotDrawn()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(0, 0, string.Empty, 1, created: 1)
            .Process(100, 1, "alpha.exe", 500, created: 10)
            .Build());

        Assert.DoesNotContain(
            Enumerable.Range(0, tree.NodeCount),
            node => tree.ProcessOf(node)?.ProcessId == 0);
    }

    /// <summary>
    /// The two tied processes have their identifiers, their creation times and their places in the
    /// snapshot all in the opposite order to their names, so a tie broken by anything but the name puts
    /// them the other way round.
    /// </summary>
    [Fact]
    public void ChildrenAreLargestFirstWithTiesInNameOrder()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(200, 1, "beta.exe", 100, created: 20)
            .Process(100, 1, "gamma.exe", 200, created: 10)
            .Process(300, 1, "alpha.exe", 100, created: 30)
            .Build());

        var applications = Node(tree, MemoryPart.Applications);

        Assert.Equal(
            ["gamma.exe", "alpha.exe", "beta.exe"],
            tree.ChildrenOf(applications).ToArray().Select(tree.NameOf));
    }

    /// <summary>
    /// Two processes each naming the other, created at the same tick, which the creation times alone
    /// cannot settle. Each is drawn exactly once, and neither is lost.
    /// </summary>
    [Fact]
    public void ProcessesNamingEachOtherAsParentAreEachDrawnOnce()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 200, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 10)
            .Build());

        Assert.Single(Enumerable.Range(0, tree.NodeCount), node => tree.KeyOf(node) == new MemoryNodeKey(MemoryPart.Process, 100, 10));
        Assert.Single(Enumerable.Range(0, tree.NodeCount), node => tree.KeyOf(node) == new MemoryNodeKey(MemoryPart.Process, 200, 10));
        Assert.Equal(400 * MiB, tree.SizeOf(Node(tree, MemoryPart.Applications)));
    }

    /// <summary>
    /// Two processes naming each other, listed after a child of one of them. The cycle is opened at a
    /// process on it, so the child stays under the parent its own creation time allows rather than
    /// being lifted to the top because of where Windows happened to list it.
    /// </summary>
    [Fact]
    public void ACycleIsOpenedWithoutMovingWhatHangsBelowIt()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(300, 100, "gamma.exe", 50, created: 20)
            .Process(100, 200, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 10)
            .Build());

        Assert.Equal(
            Node(tree, MemoryPart.Process, 100, 10),
            tree.ParentOf(Node(tree, MemoryPart.Process, 300, 20)));
        Assert.Equal(
            Node(tree, MemoryPart.Applications),
            tree.ParentOf(Node(tree, MemoryPart.Process, 100, 10)));
        Assert.Equal(450 * MiB, tree.SizeOf(Node(tree, MemoryPart.Applications)));
    }

    [Fact]
    public void EveryNodeIsFoundByItsOwnKey()
    {
        var tree = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 100, "beta.exe", 100, created: 20)
            .Process(300, 1, "gamma.exe", 50, created: 30)
            .Service("ExampleIndexer", host: 300)
            .Lists(zeroedMiB: 300, freeMiB: 500, modifiedMiB: 100, standbyMiB: 4_000)
            .Build());

        Assert.All(Enumerable.Range(0, tree.NodeCount), node => Assert.Equal(node, tree.Find(tree.KeyOf(node))));
    }

    private static int Node(MemoryTree tree, MemoryPart part) =>
        tree.Find(MemoryNodeKey.Of(part)) ?? throw new InvalidOperationException($"The tree has no {part}.");

    private static int Node(MemoryTree tree, MemoryPart part, int processId, long created) =>
        tree.Find(new MemoryNodeKey(part, processId, created))
            ?? throw new InvalidOperationException($"The tree has no {part} for process {processId}.");

    private static IEnumerable<MemoryPart> PartsUnder(MemoryTree tree, int node) =>
        tree.ChildrenOf(node).ToArray().Select(tree.PartOf);
}
