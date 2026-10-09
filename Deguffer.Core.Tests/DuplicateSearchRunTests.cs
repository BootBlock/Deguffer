using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
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
            ct => MachineProtections.ForAsync(_tree.System, _tree.Environment, _tree.Volumes, _tree.Registry, [], ct),
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
}
