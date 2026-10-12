using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What going through a searched tree costs for each file: whether a place is passed over depends
/// almost wholly on the folder holding it, so it is worked out once a folder, not once a file.
/// </summary>
public sealed class CandidateWalkCostTests
{
    private static readonly LocalVolume DriveX = new(@"X:\", DriveType.Fixed, VolumeReadiness.Ready);

    /// <summary>
    /// A file in a folder no rule reaches costs its walk nothing. Asking each file whether it was in a
    /// program folder built its path and a closure, and compared its folder with every program folder
    /// by building that folder's path and a separator once more, which measured about 3.5 KB a file with
    /// 50 program folders.
    /// </summary>
    [Fact]
    public void AFileInAFolderNoRuleReachesAllocatesNothing()
    {
        const int Folders = 100;
        const int FilesInEach = 1000;
        const int Programs = 50;

        var builder = new ExploreTreeBuilder(@"X:\Data");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, Folders).Select(folder => new ExploreChild($"folder-{folder:D3}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var folder = 0; folder < Folders; folder++)
        {
            // Empty, so the walk keeps none of them and what it allocates is what it asks of each.
            builder.AddChildren(
                first + folder,
                [.. Enumerable.Range(0, FilesInEach).Select(file => new ExploreChild($"a-file-named-as-a-camera-names-one-{file:D4}.jpg", IsDirectory: false, IsLink: false, Size: 0))]);
        }

        var tree = builder.Build(ExploreChildOrder.BySize);
        var volumes = new FakeVolumeInventory();
        var root = new ResolvedLocation(new(@"X:\Data"), @"X:\Data", ReachedFolder.At(@"X:\Data", volumes), LocationRole.Search, DriveX);

        ProgramFolder[] programs =
        [
            .. Enumerable.Range(0, Programs).Select(program =>
            {
                var place = $@"X:\Program Files\Program {program:D2}";
                return new ProgramFolder(place, ReachedFolder.At(place, volumes), $"Program {program:D2}", Final: null);
            }),
        ];

        var places = new PassedOverPlaces(new ExploreActionPolicy([], [], volumes), programs);
        var passOver = new PassedOverBelow(new UnresolvedReferences([]), places.Within(root.Folder));
        var scan = new ScannedFolder(tree, tree.RootNode, ScanStrategy.MasterFileTable, FallbackReason.None, FromIncompleteTable: false);

        Read(scan, root, passOver);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var walk = Read(scan, root, passOver);
        var perFile = (GC.GetAllocatedBytesForCurrentThread() - before) / (Folders * FilesInEach);

        Assert.Equal(Folders * FilesInEach, walk.LeftOut.Empty);
        Assert.Empty(walk.PassedOver);

        // What is left is each folder's own, its path and its comparison with each program folder,
        // which measured 3 bytes a file here.
        Assert.InRange(perFile, 0, 8);
    }

    private static CandidateWalk Read(ScannedFolder scan, ResolvedLocation root, PassedOverBelow passOver)
    {
        var walk = new CandidateWalk(new DuplicateSearch(MatchCriteria.Size, [root.Given]));
        walk.Read(scan, root, [], passOver, IdentityRoute.FileId, default);
        return walk;
    }
}
