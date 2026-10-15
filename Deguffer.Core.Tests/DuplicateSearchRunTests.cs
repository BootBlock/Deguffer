using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// One search as the page runs it (§7.4): the marks arrive before the first group, holding no group
/// yet, so the page can place each group as it is confirmed, by the space it could free.
/// </summary>
public sealed class DuplicateSearchRunTests : IDisposable
{
    private readonly DuplicateTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task TheMarksArriveEmptyBeforeTheFirstGroup()
    {
        _tree.File(new byte[] { 1, 2, 3 }, "Data", "a.bin");
        _tree.File(new byte[] { 1, 2, 3 }, "Data", "Other", "a.bin");
        _tree.File(new byte[] { 4, 5 }, "Data", "b.bin");
        _tree.File(new byte[] { 4, 5 }, "Data", "Other", "b.bin");

        List<string> happened = [];
        DuplicateMarks? made = null;
        var groupsWhenMade = -1;
        CandidateFinding? reported = null;

        var run = new DuplicateSearchRun(
            _tree.Searcher(),
            (_, ct) => MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, _tree.Registry, [], ct),
            _tree.Environment,
            new FakeCloudFiles(),
            _tree.Volumes,
            new VolumeMediaCache(new FakeStorageQueries()));

        var result = await run.RunAsync(
            new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(Path.Combine(_tree.Top, "Data"))]),
            marks =>
            {
                lock (happened)
                {
                    made = marks;
                    groupsWhenMade = marks.Groups.Count;
                    happened.Add("marks");
                }
            },
            new CallbackProgress<CandidateFinding>(finding => reported = finding),
            new CallbackProgress<DuplicateGroup>(_ =>
            {
                lock (happened)
                {
                    happened.Add("group");
                }
            }),
            progress: null,
            CancellationToken.None);

        Assert.NotNull(made);
        Assert.Equal(0, groupsWhenMade);
        Assert.Equal(["marks", "group", "group"], happened);
        Assert.Same(result.Finding, reported);
        Assert.Equal(2, result.Groups.Count);
    }

    /// <summary>
    /// Each location is opened to learn where it is once per search: the protections are scoped to
    /// where the locations resolve, and the search is handed that answer rather than resolving again,
    /// so a run opens a location no more often than finding the candidates alone does.
    /// </summary>
    [Fact]
    public async Task ASearchLocatesEachLocationOnce()
    {
        var location = Path.Combine(_tree.Top, "Data");
        _tree.File(new byte[] { 1, 2, 3 }, "Data", "a.bin");
        _tree.File(new byte[] { 1, 2, 3 }, "Data", "Other", "a.bin");
        var opened = 0;

        var files = new FileInformation(
            (path, use) =>
            {
                if (use is HandleUse.Resolve && LongPath.Display(path).Equals(location, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref opened);
                }

                return FileInformation.Open(path, use);
            },
            FileInformation.ReadIdentity);

        var search = new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(location)]);
        await _tree.Finder(files: files).FindAsync(search, _tree.Policy());
        var findingAlone = opened;
        opened = 0;

        var run = new DuplicateSearchRun(
            new DuplicateSearcher(_tree.Finder(files: files), new VolumeMediaCache(new FakeStorageQueries()), ContentReader.Default.Read, remembered: null),
            (_, ct) => MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, _tree.Registry, [], ct),
            _tree.Environment,
            new FakeCloudFiles(),
            _tree.Volumes,
            new VolumeMediaCache(new FakeStorageQueries()));

        await run.RunAsync(
            search,
            _ => { },
            finding: null,
            found: null,
            progress: null,
            CancellationToken.None);

        Assert.True(findingAlone > 0);
        Assert.Equal(findingAlone, opened);
    }

    /// <summary>
    /// The protections a search is judged by look only where its locations are: a search of a folder
    /// outside the profile does not look for applications' folders in the profile's application data,
    /// and one of the profile does. The marks hold the folders the search read, which a confirmation
    /// and a removal judge them within.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASearchIsJudgedWithinItsOwnLocations(bool searchesTheProfile)
    {
        var location = searchesTheProfile ? _tree.Environment.UserProfile : Path.Combine(_tree.Top, "Data");
        _tree.File(new byte[] { 1, 2, 3 }, Path.Combine(location, "a.bin"));
        _tree.File(new byte[] { 1, 2, 3 }, Path.Combine(location, "Other", "a.bin"));

        ProtectionScope? asked = null;
        DuplicateMarks? made = null;

        var run = new DuplicateSearchRun(
            _tree.Searcher(),
            (scope, ct) =>
            {
                asked = scope;
                return MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, _tree.Registry, [], ct);
            },
            _tree.Environment,
            new FakeCloudFiles(),
            _tree.Volumes,
            new VolumeMediaCache(new FakeStorageQueries()));

        await run.RunAsync(
            new DuplicateSearch(MatchCriteria.Content, [new SearchLocation(location)]),
            marks => made = marks,
            finding: null,
            found: null,
            progress: null,
            CancellationToken.None);

        var expected = searchesTheProfile ? FolderReach.Reached : FolderReach.Outside;

        Assert.NotNull(asked);
        Assert.NotNull(made);
        Assert.Equal(FolderReach.Reached, (await asked.ReachAsync(location)).Reach);
        Assert.Equal(expected, (await asked.ReachAsync(_tree.Environment.LocalAppData)).Reach);
        Assert.Equal(FolderReach.Reached, (await made.Searched.ReachAsync(Path.Combine(location, "Other"))).Reach);
        Assert.Equal(expected, (await made.Searched.ReachAsync(_tree.Environment.RoamingAppData)).Reach);
    }
}
