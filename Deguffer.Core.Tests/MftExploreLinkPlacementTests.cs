using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a read of the file table places a hard-linked file's other names once the parse threads
/// have finished (#488): each as a node numbered after every record, in an order the parse threads
/// cannot change, and without copying the columns sized to the whole table to make room for them.
/// </summary>
public sealed class MftExploreLinkPlacementTests
{
    private const uint First = 16;
    private const uint Second = 17;
    private const uint Orphan = 18;

    private const string Root = @"C:\";
    private const string FirstPath = @"C:\first";
    private const string SecondPath = @"C:\second";

    /// <summary>
    /// The records keep their order, and every other name comes after them: by record, then by the
    /// folder holding it, then by name, however the four parse threads met them. A name in a folder
    /// the root does not reach is left out with its folder. A tree's node numbers are the order its
    /// reader numbered them in, so this holds the numbering itself.
    /// </summary>
    [Fact]
    public void NumbersEachOtherNameAfterEveryRecordByRecordThenFolderThenName()
    {
        using var source = new MftFixture()
            .AddDirectory(First, MftRecord.RootRecordNumber, "first")
            .AddDirectory(Second, MftRecord.RootRecordNumber, "second")
            .AddDirectory(Orphan, 9000, "orphan")
            .AddFile(20, First, "plain.txt", allocated: 512, logical: 500)
            .AddHardLinkedFile(21, [(First, "x.dll"), (Second, "m.dll"), (First, "n.dll"), (Orphan, "lost.dll")], allocated: 4096, logical: 4000)
            .AddHardLinkedFile(22, [(Second, "w.bin"), (First, "v.bin"), (First, "u.bin")], allocated: 2048, logical: 2000)
            .AddFile(23, Second, "last.txt", allocated: 1024, logical: 1000)
            .Build();

        // Four records a read and eight reads in flight, so the four threads parse side by side.
        var tree = MftExploreReader.Read(source, Root, [], new TableTuning(4 * 1024, 8, 4, 1), progress: null, default).Tree!;

        string[] numbered =
        [
            FirstPath,
            SecondPath,
            FirstPath + @"\plain.txt",
            FirstPath + @"\x.dll",
            SecondPath + @"\w.bin",
            SecondPath + @"\last.txt",
            FirstPath + @"\n.dll",
            SecondPath + @"\m.dll",
            FirstPath + @"\u.bin",
            FirstPath + @"\v.bin",
        ];

        var nodes = numbered.Select(path => TreeNodes.At(tree, path)).ToArray();

        Assert.Equal(nodes.Order(), nodes);
        Assert.Equal(nodes.Length, nodes.Distinct().Count());
        Assert.Equal(512 + 4096 + 4096 + 2048 + 2048, tree.SizeOf(TreeNodes.At(tree, FirstPath)));
        Assert.Equal(4096 + 2048 + 1024, tree.SizeOf(TreeNodes.At(tree, SecondPath)));
        Assert.DoesNotContain(
            Enumerable.Range(0, tree.NodeCount),
            node => tree.NameOf(node) is "lost.dll" or "orphan");
    }

    /// <summary>
    /// One other name in a table costs the read about what its node costs, not a copy of every column
    /// the table was read into. Those columns are sized to every record, so making room for the names
    /// after them by growing each was nine copies at 35 bytes a record in all, on every system volume,
    /// before assembling copied the same columns again to compact them.
    ///
    /// <para>Measured on this thread, which places the names and assembles the tree, against the same
    /// table read with no other name. The bound is a byte a record: the name's node and its own column
    /// slots are a few hundred bytes in all, and growing the columns again would be 35 a record.</para>
    ///
    /// <para>Each table is measured as the least of several reads (#757). Assembling orders names and
    /// children in parallel loops, and now and then the thread pool's own bookkeeping for those loops
    /// lands on this thread, about 7 KB at a time. That is added to a read, never taken from one, so
    /// the least of a few reads is the read without it, and the two tables then differ by the few
    /// hundred bytes the name costs.</para>
    /// </summary>
    [Fact]
    public void ATableWithAnotherNameDoesNotCopyItsColumnsToMakeRoomForIt()
    {
        const int Files = 20_000;
        const int Reads = 5;

        using var plain = Table(linked: false);
        using var linked = Table(linked: true);

        var extra = Least(linked) - Least(plain);

        Assert.InRange(extra, 0, Files);

        static long Least(IMftSource source)
        {
            // The first read also pays for warming the reader up, which only raises it, so one read
            // more than Reads is taken.
            var least = long.MaxValue;

            for (var read = 0; read <= Reads; read++)
            {
                least = Math.Min(least, Allocated(source));
            }

            return least;
        }

        static IMftSource Table(bool linked)
        {
            var fixture = new MftFixture()
                .AddDirectory(First, MftRecord.RootRecordNumber, "first")
                .AddDirectory(Second, MftRecord.RootRecordNumber, "second");

            for (var file = 0u; file < Files; file++)
            {
                var record = MftRecord.ReservedRecordCount + 8 + file;
                var name = $"file-{file}.bin";

                if (linked && file == 0)
                {
                    fixture.AddHardLinkedFile(record, [(First, name), (Second, name)], allocated: 4096, logical: 4000);
                }
                else
                {
                    fixture.AddFile(record, First, name, allocated: 4096, logical: 4000);
                }
            }

            return fixture.Build();
        }

        static long Allocated(IMftSource source)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var tree = MftExploreReader.Read(source, Root, [], TableTuning.Default, progress: null, default).Tree!;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            GC.KeepAlive(tree);
            return allocated;
        }
    }

    /// <summary>
    /// The links are joined to the records where the root reaches every node too, where nothing is
    /// left out and so nothing else would compact the columns. Left as they were, the record columns
    /// would hold no slot for a link the child lists name.
    /// </summary>
    [Fact]
    public void JoinsTheLinksToTheRecordsWhereTheRootReachesEveryNode()
    {
        var names = new NamePool();
        var records = new MftExploreColumns(3);

        Describe(records, 0, parent: 0, names.Intern(Root), ExploreNodeFlags.Directory, size: 0);
        Describe(records, 1, parent: 0, names.Intern("first"), ExploreNodeFlags.Directory, size: 0);
        Describe(records, 2, parent: 1, names.Intern("shared.dll"), ExploreNodeFlags.None, size: 4096);

        var links = new MftExploreColumns(1);
        Describe(links, 0, parent: 0, names.Intern("shared.dll"), ExploreNodeFlags.None, size: 4096);

        var tree = ExploreTree.Create(
            Root, 0, records.NameNumbers, names.Freeze(), records.Parents, records.Sizes, records.Lengths,
            records.Storage, records.Flags, records.Created, records.Modified, records.Visibility,
            ExploreChildOrder.BySize, links: links);

        Assert.Equal(4, tree.NodeCount);
        Assert.Equal(4096, tree.SizeOf(TreeNodes.At(tree, Root + "shared.dll")));
        Assert.Equal(4096, tree.SizeOf(TreeNodes.At(tree, FirstPath + @"\shared.dll")));
        Assert.Equal(2 * 4096, tree.TotalBytes);

        static void Describe(MftExploreColumns columns, int slot, int parent, int name, ExploreNodeFlags kind, long size)
        {
            columns.NameNumbers[slot] = name;
            columns.Parents[slot] = parent;
            columns.Sizes[slot] = size;
            columns.Flags[slot] = ExploreNodeFlags.Present | kind;
        }
    }
}
