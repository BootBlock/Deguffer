using System.Runtime.CompilerServices;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What grouping the files of a search by what the tree holds gives, and what it costs: a drive holds
/// millions of files, and this runs between the end of the walk and the first progress of identifying.
/// </summary>
public sealed class CandidateGroupingTests
{
    private static readonly LocalVolume DriveX = new(@"X:\", DriveType.Fixed, VolumeReadiness.Ready);

    private static readonly ExploreTimestamp[] Times =
    [
        default,
        ExploreTimestamp.FromUtc(new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc)),
        ExploreTimestamp.FromUtc(new DateTime(2026, 3, 14, 15, 9, 59, DateTimeKind.Utc)),
        ExploreTimestamp.FromUtc(new DateTime(2026, 3, 14, 15, 10, 0, DateTimeKind.Utc)),
    ];

    /// <summary>
    /// The walk keeps one for each file that passes the filters, so what is shared by every file under
    /// a location is held once for that location and not in each.
    /// </summary>
    [Fact]
    public void AFoundFileHoldsNothingEveryFileUnderItsLocationShares() =>
        Assert.Equal(16, Unsafe.SizeOf<FoundFile>());

    /// <summary>
    /// Most sizes on a disk are held by one file, and nothing is built for those. The cost is one
    /// flat record for each file to sort, 24 bytes each, so 2.4 MB here.
    /// </summary>
    [Fact]
    public void GroupingFilesOfMostlyUniqueLengthsBuildsNothingForTheUniqueOnes()
    {
        const int Files = 100_000;
        const int SharedEvery = 10_000;

        var found = Walked(
            MatchCriteria.Size,
            [.. Enumerable.Range(0, Files).Select(file => File($"file-{file}.bin", SharedEvery * (file / SharedEvery) == file - 1 ? file : file + 1))]);

        CandidateGrouping.ByTheTree(found, MatchCriteria.Size, CandidateGrouping.TreeMinuteOf);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var groups = CandidateGrouping.ByTheTree(found, MatchCriteria.Size, CandidateGrouping.TreeMinuteOf);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Files / SharedEvery, groups.Count);
        Assert.All(groups, group => Assert.Equal(2, group.Count));
        Assert.InRange(allocated, 0, 5 * 1024 * 1024);
    }

    /// <summary>
    /// Every criterion and every pair of them, against the plain meaning of a grouping: equal lengths
    /// and minutes, names equal ordinally ignoring case, a file the tree holds no time for left out
    /// where the time is compared, and groups and their files in the order they were found, so the
    /// order files are identified in does not follow how their lengths happen to sort.
    /// </summary>
    [Theory]
    [InlineData(MatchCriteria.Size)]
    [InlineData(MatchCriteria.Content)]
    [InlineData(MatchCriteria.Name)]
    [InlineData(MatchCriteria.Modified)]
    [InlineData(MatchCriteria.Size | MatchCriteria.Name)]
    [InlineData(MatchCriteria.Size | MatchCriteria.Modified)]
    [InlineData(MatchCriteria.Name | MatchCriteria.Modified)]
    [InlineData(MatchCriteria.Size | MatchCriteria.Name | MatchCriteria.Modified)]
    public void FilesAreGroupedAsTheCriteriaSayInTheOrderTheyWereFound(MatchCriteria criteria)
    {
        string[] names = ["a.bin", "A.BIN", "b.bin", "é.bin", "É.BIN", "ss.bin", "ß.bin"];
        var random = new Random(683);

        var found = Walked(
            criteria,
            [
                .. Enumerable.Range(0, 3000).Select(_ =>
                    File(names[random.Next(names.Length)], random.Next(1, 40), Times[random.Next(Times.Length)])),
            ]);

        var bySize = criteria.GroupsBySize();
        var byName = criteria.GroupsByName();
        var byTime = criteria.GroupsByTime();

        int[][] expected =
        [
            .. found
                .Where(file => !byTime || CandidateGrouping.TreeMinuteOf(file) is not null)
                .GroupBy(file => (
                    Length: bySize ? file.Tree.LengthOf(file.Node) : 0,
                    Name: byName ? file.Tree.NameOf(file.Node).ToUpperInvariant() : string.Empty,
                    Minute: byTime ? CandidateGrouping.TreeMinuteOf(file) : 0))
                .Where(group => group.Count() > 1)
                .Select(group => group.Select(file => file.Node).ToArray()),
        ];

        var groups = CandidateGrouping.ByTheTree(found, criteria, CandidateGrouping.TreeMinuteOf);

        Assert.True(expected.Length > 1);
        Assert.Equal(expected, groups.Select(group => group.Select(file => file.Node).ToArray()));
    }

    private static ExploreChild File(string name, long length, ExploreTimestamp modified = default) =>
        new(name, IsDirectory: false, IsLink: false, length, LastWritten: modified, Length: length);

    private static IReadOnlyList<FoundFile> Walked(MatchCriteria criteria, ExploreChild[] files)
    {
        var builder = new ExploreTreeBuilder(@"X:\Data");
        builder.AddChildren(ExploreTreeBuilder.RootNode, files);

        var tree = builder.Build(ExploreChildOrder.ByName);
        var scan = new ScannedFolder(tree, tree.RootNode, ScanStrategy.MasterFileTable, FallbackReason.None, FromIncompleteTable: false);
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", new FakeVolumeInventory()), LocationRole.Search, DriveX);
        var walk = new CandidateWalk(new DuplicateSearch(criteria, [root.Given]));

        walk.Read(scan, root, [], new PassedOverBelow(new UnresolvedReferences([]), below: null), IdentityRoute.FileId, default);

        return walk.Found;
    }
}
