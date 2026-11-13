using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A file-table read's snapshots copy only the flags and parents, read every other column where the
/// parse threads write it, and reuse what they assemble with from one snapshot to the next (#1015).
/// </summary>
public sealed class MftSnapshotReuseTests
{
    private const string Root = @"C:\";
    private const int VolumeRoot = (int)MftRecord.RootRecordNumber;

    private static readonly ExploreTimestamp Older = ExploreTimestamp.FromUtc(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    private static readonly ExploreTimestamp Newer = ExploreTimestamp.FromUtc(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// After the first, which makes what the snapshots draw from, no snapshot allocates anything sized
    /// to the table: only what its tree keeps. Each used to copy all nine columns of every slot, 35
    /// bytes a slot, and make child starts, an order and a renumbering four bytes a slot each, so the
    /// bound here, a quarter of a byte a slot, is a sixth of a percent of what one cost.
    ///
    /// <para>Measured on the thread that assembles each snapshot, which is the thread that reports,
    /// because the snapshots are assembled on the spot.</para>
    /// </summary>
    [Fact]
    public void NoSnapshotAfterTheFirstAllocatesAnythingSizedToTheTable()
    {
        const uint lastRecord = (4 * MftPassProgress.Interval) + 10;
        using var source = new MftFixture()
            .AddDirectory(16, MftRecord.RootRecordNumber, "Users")
            .AddDirectory(17, 16, "testuser")
            .AddFile(18, 17, "notes.txt", allocated: 4096, logical: 4000)
            .AddFile(19, 17, "music.flac", allocated: 8192, logical: 8000)
            .AddUnused(lastRecord)
            .WithoutBitmap()
            .Build();

        var allocations = new List<long>();
        var trees = new List<ExploreTree>();
        var clock = new ManualTimeProvider();
        var progress = new CallbackProgress<ExploreProgress>(report =>
        {
            if (report.Snapshot is { } tree)
            {
                trees.Add(tree);
            }

            clock.Advance(ScanSnapshots.Interval);
        });

        var assembly = new SnapshotAssembly(
            assemble =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                assemble();
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);

                return Task.CompletedTask;
            },
            () => ExploreChildOrder.ByName);

        MftExploreReader.Read(source, Root, [], TableTuning.Default, progress, default, assembly, clock);

        Assert.True(allocations.Count >= 3, $"The read assembled {allocations.Count} snapshots, not one at each of four reports.");
        Assert.All(trees, tree => Assert.Equal(4096 + 8192, tree.TotalBytes));

        foreach (var allocated in allocations.Skip(1))
        {
            Assert.InRange(allocated, 0, lastRecord / 4);
        }
    }

    /// <summary>
    /// A slot whose flags are not yet written is drawn as nothing, whatever its other columns hold,
    /// and so is the volume's root, which is forced present before its record is read: its size,
    /// dates and visibility are none of its own until its flags say it was described.
    /// </summary>
    [Fact]
    public void ASnapshotDrawsNothingOfASlotStillBeingDescribed()
    {
        var (columns, names) = Table(count: 10);
        var file = names.Intern("notes.txt");

        Describe(columns, 6, VolumeRoot, names.Intern("Users"), ExploreNodeFlags.Directory, size: 0);
        Describe(columns, 7, 6, file, ExploreNodeFlags.None, size: 100, Older);

        // Half described: every column but the flags.
        WriteAllButFlags(columns, VolumeRoot, VolumeRoot, names.Intern("root"), size: 999, Newer);
        WriteAllButFlags(columns, 8, 6, names.Intern("half.bin"), size: 5000, Newer);

        var tree = Snapshot(columns, names, new MftSnapshotColumns(columns.Count));

        Assert.Equal(3, tree.NodeCount);
        Assert.Equal(100, tree.TotalBytes);
        Assert.Equal(100, tree.LengthOf(tree.RootNode));
        Assert.Equal(Older, tree.ModifiedOf(tree.RootNode));
        Assert.Equal(default, tree.CreatedOf(tree.RootNode));
        Assert.Equal(FileVisibility.Shown, tree.VisibilityOf(tree.RootNode));
        Assert.Equal(Root, tree.NameOf(tree.RootNode));
        Assert.Null(ExploreTreePaths.TryNodeAt(tree, Root + @"Users\half.bin"));
    }

    /// <summary>
    /// A snapshot writes nothing to the columns the parse threads are filling, even where its root
    /// reaches every slot and nothing would otherwise be left out: the totals, the root's name and
    /// parent, and the volume root's forced flags all go into what the snapshot owns.
    /// </summary>
    [Fact]
    public void ASnapshotWritesNothingToTheColumnsTheReadIsFilling()
    {
        var (columns, names) = Table(count: 8);

        Describe(columns, VolumeRoot, parent: 0, names.Intern("."), ExploreNodeFlags.None, size: 0);

        for (var slot = 0; slot < columns.Count; slot++)
        {
            if (slot != VolumeRoot)
            {
                Describe(columns, slot, VolumeRoot, names.Intern($"file-{slot}"), ExploreNodeFlags.None, size: 10 + slot, Older);
            }
        }

        var before = Copy(columns);
        var tree = Snapshot(columns, names, new MftSnapshotColumns(columns.Count));

        Assert.Equal(columns.Count, tree.NodeCount);
        Assert.Equal(Enumerable.Range(0, columns.Count).Where(slot => slot != VolumeRoot).Sum(slot => 10L + slot), tree.TotalBytes);
        Assert.Equal(before, Copy(columns));
    }

    /// <summary>
    /// A run of snapshots drawn from one set of reused room, as the read keeps it, draws each tree as
    /// a snapshot drawn from new room would: the same nodes, names, totals, dates and order of
    /// children, by name and by size. Between them, records arrive under the scan's folder and
    /// outside it, with names never met before, and a scan of one folder keeps only its own names, so
    /// the names are numbered again in each tree while the sort keys kept between snapshots are
    /// numbered as the read numbers them.
    /// </summary>
    [Theory]
    [InlineData(ExploreChildOrder.ByName)]
    [InlineData(ExploreChildOrder.BySize)]
    public void ASnapshotDrawnFromReusedRoomDrawsWhatOneDrawnAfreshDraws(ExploreChildOrder order)
    {
        const int count = 400;
        var (columns, names) = Table(count);
        var reused = new MftSnapshotColumns(count);
        var next = 16;

        Describe(columns, next++, VolumeRoot, names.Intern("Users"), ExploreNodeFlags.Directory, size: 0);
        var users = next - 1;

        foreach (var (inside, outside) in new[] { (5, 60), (12, 90), (1, 120), (20, 0) })
        {
            for (var i = 0; i < inside; i++, next++)
            {
                // Names that sort differently by key and by number, and some alike for more than the
                // sixteen characters a key holds.
                var name = (next % 3) switch
                {
                    0 => $"zeta-{count - next}",
                    1 => $"a-very-long-shared-prefix-{next}",
                    _ => $"Mid-{next}",
                };

                Describe(columns, next, users, names.Intern(name), ExploreNodeFlags.None, size: 512 * (next % 4), next % 2 == 0 ? Older : Newer);
            }

            for (var i = 0; i < outside; i++, next++)
            {
                Describe(columns, next, VolumeRoot, names.Intern($"elsewhere-with-a-long-name-{next}"), ExploreNodeFlags.None, size: 1);
            }

            var fromReused = Snapshot(columns, names, reused, order, ["Users"]);
            var afresh = Snapshot(columns, names, new MftSnapshotColumns(count), order, ["Users"]);

            Assert.Equal(Drawn(afresh), Drawn(fromReused));
        }
    }

    private static (MftExploreColumns Columns, NamePool Names) Table(int count) => (new MftExploreColumns(count), new NamePool());

    private static ExploreTree Snapshot(
        MftExploreColumns columns,
        NamePool names,
        MftSnapshotColumns own,
        ExploreChildOrder order = ExploreChildOrder.ByName,
        string[]? components = null)
    {
        components ??= [];
        var rootPath = Path.Combine([Root, .. components]);

        columns.CopyPresent(own, CancellationToken.None);
        var rootName = names.Intern(rootPath);

        return MftExploreRooting.Assemble(
            columns,
            links: null,
            own,
            names.Freeze(),
            rootName,
            rootPath,
            components,
            new MftExploreRooting.AssembledAs(order, LowerBound: true, MatchCaseOnly: true),
            CancellationToken.None).Tree ?? throw new Xunit.Sdk.XunitException("The snapshot found no root.");
    }

    private static void Describe(
        MftExploreColumns columns, int slot, int parent, int name, ExploreNodeFlags kind, long size, ExploreTimestamp modified = default)
    {
        WriteAllButFlags(columns, slot, parent, name, size, modified);
        columns.Flags[slot] = ExploreNodeFlags.Present | kind;
    }

    private static void WriteAllButFlags(MftExploreColumns columns, int slot, int parent, int name, long size, ExploreTimestamp modified)
    {
        columns.NameNumbers[slot] = name;
        columns.Parents[slot] = parent;
        columns.Sizes[slot] = size;
        columns.Lengths[slot] = size;
        columns.Storage[slot] = FileStorage.Compressed;
        columns.Created[slot] = modified;
        columns.Modified[slot] = modified;
        columns.Visibility[slot] = FileVisibility.Hidden;
    }

    private static string Copy(MftExploreColumns columns) => string.Join(
        '|',
        string.Join(',', columns.NameNumbers),
        string.Join(',', columns.Parents),
        string.Join(',', columns.Sizes),
        string.Join(',', columns.Lengths),
        string.Join(',', columns.Storage),
        string.Join(',', columns.Flags),
        string.Join(',', columns.Created),
        string.Join(',', columns.Modified),
        string.Join(',', columns.Visibility));

    /// <summary>Every node from the root down, each with its figures and its children in order.</summary>
    private static List<string> Drawn(ExploreTree tree)
    {
        var drawn = new List<string>();
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            var children = tree.ChildrenOf(node).ToArray();

            drawn.Add(string.Join(
                ' ',
                tree.PathOf(node),
                tree.SizeOf(node),
                tree.LengthOf(node),
                tree.StorageOf(node),
                tree.ModifiedOf(node),
                tree.CreatedOf(node),
                tree.VisibilityOf(node),
                tree.HasUnknownSizeBelow(node),
                string.Join('/', children.Select(tree.NameOf))));

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }

        return drawn;
    }
}
