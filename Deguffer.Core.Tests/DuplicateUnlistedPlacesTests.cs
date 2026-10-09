using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk is told what a search passes over and does not list it (§7.4, and the walk measured in
/// <c>docs/todo/done/duplicates.md</c>, phase 3): the places passed over by default were 41% of a warm
/// walk of a system drive. The search must come out exactly as it did when the walk listed them.
/// </summary>
public sealed class DuplicateUnlistedPlacesTests : IDisposable
{
    private const int Length = 64;

    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static ExploreScanner Walking() => new(FakeMftSourceFactory.Unavailable(FallbackReason.NotElevated));

    /// <summary>
    /// A folder the walk is told to leave unlisted is in the tree, so it can be named, with nothing
    /// listed below it although it holds files; its sibling is listed as before. The rule is asked
    /// with the folder holding it and its own path in display form.
    /// </summary>
    [Fact]
    public async Task AFolderLeftUnlistedIsInTheTreeWithNothingBelowIt()
    {
        _tree.File(Length, "Data", "Skip", "Inner", "a.bin");
        _tree.File(Length, "Data", "Keep", "b.bin");
        var data = Path.Combine(_tree.Top, "Data");
        List<(string Parent, bool ParentIsRoot, string Folder)> asked = [];

        var scan = Assert.Single(await Walking().ScanFoldersAsync(
            [data],
            [(parent, parentIsRoot, folder, name) =>
            {
                lock (asked)
                {
                    asked.Add((parent, parentIsRoot, folder));
                }

                return name == "Skip";
            }],
            progress: null,
            default));

        var tree = scan.Tree;
        var skip = Child(tree, scan.Node, "Skip");
        Assert.True(tree.IsDirectory(skip));
        Assert.Empty(tree.ChildrenOf(skip).ToArray());
        Assert.False(tree.ListingWasRefused(skip));
        Assert.Equal("b.bin", tree.NameOf(Assert.Single(tree.ChildrenOf(Child(tree, scan.Node, "Keep")).ToArray())));
        Assert.Contains((data, true, Path.Combine(data, "Skip")), asked);
        Assert.DoesNotContain(asked, question => question.Folder.Contains("Inner", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder Windows refuses to list inside a place left unlisted is never asked for, so nothing in
    /// the tree is marked refused, and the search names the place as passed over once and nothing in
    /// it as unread.
    /// </summary>
    [Fact]
    public async Task ARefusedFolderInsideAPassedOverPlaceIsNamedOnlyAsPassedOver()
    {
        _tree.File(Length, "Windows", "System32", "a.dll");
        var denied = Path.Combine(_tree.Top, "Windows", "Locked");
        _tree.File(Length, "Windows", "Locked", "b.dll");
        _tree.File(Length, "Data", "m.txt");
        _tree.File(Length, "Data", "Other", "m.txt");
        using var refusal = new DeniedDirectory(denied);
        var policy = _tree.Policy();

        var scan = Assert.Single(await Walking().ScanFoldersAsync(
            [_tree.Top],
            [(_, _, folder, _) => policy.RefusedAtAndBelow(folder) is not null],
            progress: null,
            default));
        var found = await _tree.FindAsync(MatchCriteria.Size, new SearchLocation(_tree.Top));

        Assert.DoesNotContain(Nodes(scan.Tree, scan.Node), node => scan.Tree.ListingWasRefused(node));
        Assert.Empty(found.Unread);
        Assert.Single(found.PassedOver, place => place.Path == _tree.System.WindowsDirectory);
        Assert.DoesNotContain(found.PassedOver, place => place.Path.StartsWith(_tree.System.WindowsDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(found.Groups).Files.Count);
    }

    /// <summary>
    /// The file table is read whole whatever the walk is told, so a search by each route gives the
    /// same groups and names the same places passed over: what the walk leaves unlisted is what the
    /// table route passes over as it reads the tree.
    /// </summary>
    [Fact]
    public async Task ASearchGivesTheSameResultsByTheTableAndByTheWalk()
    {
        _tree.File(Length, "Windows", "System32", "a.dll");
        _tree.File(Length, "Windows", "System32", "b.dll");
        _tree.File(Length, "Data", "m.txt");
        _tree.File(Length, "Data", "Other", "n.txt");
        Assert.True(VolumePath.TryParse(_tree.Top, out var top));
        var (fixture, parent, next) = Through(top.Components);
        Mirror(fixture, _tree.Top, parent, ref next);
        var search = new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(_tree.Top)]);

        var walked = await _tree.Finder().FindAsync(search, _tree.Policy());
        var read = await _tree.Finder(new ExploreScanner(FakeMftSourceFactory.Serving(top.DriveLetter, fixture), tuning: RouteTuners.Table))
            .FindAsync(search, _tree.Policy());

        Assert.NotEqual(Assert.Single(walked.Read).RouteNote, Assert.Single(read.Read).RouteNote);
        Assert.Contains(walked.PassedOver, place => place.Path == _tree.System.WindowsDirectory);
        Assert.Equal(walked.PassedOver.OrderBy(place => place.Path, StringComparer.Ordinal), read.PassedOver.OrderBy(place => place.Path, StringComparer.Ordinal));
        Assert.Equal(
            ["m.txt", "n.txt"],
            Assert.Single(walked.Groups).Files.Select(file => file.Name).Order());
        Assert.Equal(
            Assert.Single(walked.Groups).Files.Select(file => file.Path).Order(),
            Assert.Single(read.Groups).Files.Select(file => file.Path).Order());
    }

    /// <summary>
    /// The search's own walk lists nothing below a place it passes over. Its progress counts every
    /// entry the walk lists, which must be every entry on the drive except those inside a place the
    /// search named as passed over; the places themselves are listed by the folder holding them.
    /// </summary>
    [Fact]
    public async Task TheSearchsOwnWalkListsNothingBelowAPlaceItPassesOver()
    {
        for (var i = 0; i < 20; i++)
        {
            _tree.File(Length, "Windows", "System32", $"{i}.dll");
        }

        _tree.File(Length, "Data", "m.txt");
        _tree.File(Length, "Data", "Other", "m.txt");
        long listed = 0;

        var found = await _tree.Finder().FindAsync(
            new DuplicateSearch(MatchCriteria.Size, [new SearchLocation(_tree.Top)]),
            _tree.Policy(),
            new CallbackProgress<ExploreProgress>(progress => listed = Math.Max(listed, progress.Done)));

        var places = found.PassedOver.Select(place => place.Path + Path.DirectorySeparatorChar).ToList();
        var outside = Directory.EnumerateFileSystemEntries(_tree.Top, "*", SearchOption.AllDirectories)
            .Count(entry => !places.Any(place => entry.StartsWith(place, StringComparison.Ordinal)));

        Assert.Contains(found.PassedOver, place => place.Path == _tree.System.WindowsDirectory);
        Assert.Equal(outside, listed);
    }

    private static int Child(ExploreTree tree, int folder, string name) =>
        tree.ChildrenOf(folder).ToArray().Single(child => tree.NameOf(child) == name);

    private static IEnumerable<int> Nodes(ExploreTree tree, int node) =>
        [node, .. tree.ChildrenOf(node).ToArray().SelectMany(child => Nodes(tree, child))];

    /// <summary>
    /// Adds everything in <paramref name="folder"/> on disk to the table below the record
    /// <paramref name="parent"/>, so the table describes the scratch drive as the walk finds it: the
    /// fakes put Windows' folders and a Users folder there too.
    /// </summary>
    private static void Mirror(MftFixture fixture, string folder, uint parent, ref uint next)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            var record = next++;

            if (entry is FileInfo file)
            {
                fixture.AddFile(record, parent, file.Name, allocated: 4096, logical: file.Length);
            }
            else
            {
                fixture.AddDirectory(record, parent, entry.Name);
                Mirror(fixture, entry.FullName, record, ref next);
            }
        }
    }

    /// <summary>A table holding <paramref name="folders"/>, each inside the one before it, below the volume's top.</summary>
    /// <returns>The table, the record of the last folder, and the first record number still free.</returns>
    private static (MftFixture Fixture, uint Parent, uint Next) Through(IEnumerable<string> folders)
    {
        var fixture = new MftFixture();
        uint parent = MftRecord.RootRecordNumber;
        uint next = MftRecord.ReservedRecordCount;

        foreach (var folder in folders)
        {
            fixture.AddDirectory(next, parent, folder);
            parent = next++;
        }

        return (fixture, parent, next);
    }
}
