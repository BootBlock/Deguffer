using Deguffer.App.ViewModels;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Duplicates page shows what it found and what is marked at a glance: the figures over the
/// groups, the marked total, which copies stay, each group's name and mark count, and the search
/// settings folding to their one line. What a group could free and what a mark occupies are Core's
/// (<see cref="GroupMarks.FreeableSpace"/>, <see cref="GroupMarks.MarkedSpace"/>); these prove the
/// page adds up and shows Core's figures, and keeps them current.
/// </summary>
public sealed class DuplicatesPageFiguresTests : DuplicatesPageScene
{
    private DuplicateCandidate Copy(string folder, string name, long size = 4096) => _scene.Copy(Path.Combine(folder, name), size);

    private static DuplicateGroup Group(params DuplicateCandidate[] copies) => DuplicateScene.Group(MatchCriteria.Content, copies);

    private static DuplicateCopyRow Row(DuplicatesViewModel page, DuplicateCandidate copy) =>
        page.Groups.SelectMany(group => group.Copies).Single(row => row.Copy.Identity == copy.Identity);

    /// <summary>What Core says every group's standing marks occupy, which the marked total must equal.</summary>
    private static string CoreMarked(DuplicatesViewModel page, int count) =>
        $"{count} {(count == 1 ? "copy" : "copies")}, {FreeSpace.Format(page.Groups.Sum(row => row.Marks.MarkedSpace(page.Marks!.Keeping)))}";

    /// <summary>
    /// The figures count every group as it arrives, from Core's measure of each, and a new search
    /// starts them again rather than adding its groups to the last search's.
    /// </summary>
    [Fact]
    public void TheFiguresCountEveryGroupAsItArrivesAndStartAgainWithANewSearch() => UiThread.Run(async () =>
    {
        var three = Group(Copy(_scene.Folder("A"), "c.jpg", 8192), Copy(_scene.Folder("B"), "c.jpg", 8192), Copy(_scene.Folder("C"), "c.jpg", 8192));
        var page = PageWithPhotos(Finds(_scene.Pair("a.jpg", 4096), three));
        List<string> seen = [];
        page.Totals.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(DuplicateTotalsViewModel.Groups))
            {
                seen.Add(page.Totals.Groups);
            }
        };

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["0", "1", "2"], seen);
        Assert.Equal("2", page.Totals.Groups);
        Assert.Equal("groups", page.Totals.GroupsLabel);
        Assert.Equal("5", page.Totals.Files);
        Assert.Equal(FreeSpace.Format(page.Groups.Sum(row => row.Marks.FreeableSpace(page.Marks!.Keeping))), page.Totals.Freeable);
        Assert.True(page.HasGroups);
        Assert.False(page.HasNoGroups);

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal("2", page.Totals.Groups);
        Assert.Equal("5", page.Totals.Files);
    });

    /// <summary>
    /// Marking one copy by hand shows it marked, shows every other copy in its group as staying, and
    /// counts the group's marks, and unmarking it shows none of that. The copies of another group,
    /// where nothing is marked, say neither.
    /// </summary>
    [Fact]
    public void MarkingACopyShowsWhichCopiesStayAndCountsTheGroupsMarks() => UiThread.Run(async () =>
    {
        var three = Group(Copy(_scene.Folder("A"), "c.jpg"), Copy(_scene.Folder("B"), "c.jpg"), Copy(_scene.Folder("C"), "c.jpg"));
        var other = _scene.Pair("d.jpg", 2048);
        var page = PageWithPhotos(Finds(three, other));
        await page.SearchCommand.ExecuteAsync(null);
        var sibling = Row(page, three.Files[0]);
        List<string?> told = [];
        sibling.PropertyChanged += (_, changed) => told.Add(changed.PropertyName);

        Row(page, three.Files[1]).ToggleCommand.Execute(null);

        Assert.Equal([false, true, false], three.Files.Select(copy => Row(page, copy).IsMarked));
        Assert.Equal([true, false, true], three.Files.Select(copy => Row(page, copy).Stays));
        Assert.Contains(nameof(DuplicateCopyRow.Stays), told);
        Assert.Contains("Stays.", sibling.Description, StringComparison.Ordinal);
        Assert.Contains("Marked for removal.", Row(page, three.Files[1]).Description, StringComparison.Ordinal);
        Assert.All(other.Files, copy => Assert.False(Row(page, copy).Stays));

        var group = Row(page, three.Files[1]).Owner;
        Assert.Equal("1 of 3 marked", group.MarkedCount);
        Assert.True(group.HasMarks);

        Row(page, three.Files[1]).ToggleCommand.Execute(null);

        Assert.All(three.Files, copy => Assert.False(Row(page, copy).Stays));
        Assert.Equal(string.Empty, group.MarkedCount);
        Assert.False(group.HasMarks);
    });

    /// <summary>
    /// The marked total is what Core says the standing marks occupy, after a mark by hand, after a
    /// rule and after the marks are cleared, and a new search drops the last search's.
    /// </summary>
    [Fact]
    public void TheMarkedTotalIsWhatCoreSaysTheMarksOccupy() => UiThread.Run(async () =>
    {
        var small = _scene.Pair("a.jpg", 4096);
        var large = _scene.Pair("b.jpg", 65536);
        var page = PageWithPhotos(Finds(small, large));
        await page.SearchCommand.ExecuteAsync(null);
        Assert.Equal("None", page.Marking.MarkedFigure);

        Row(page, large.Files[1]).ToggleCommand.Execute(null);

        Assert.Equal(CoreMarked(page, 1), page.Marking.MarkedFigure);
        Assert.Contains(FreeSpace.Format(65536), page.Marking.MarkedFigure, StringComparison.Ordinal);

        page.Marking.RuleIndex = 4;
        page.Marking.RuleFolder = _scene.Folder("Downloads");
        await page.Marking.RunRuleCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Groups.Sum(row => row.Marks.MarkedCount));
        Assert.Equal(CoreMarked(page, 2), page.Marking.MarkedFigure);
        Assert.All(page.Groups, row => Assert.Equal("1 of 2 marked", row.MarkedCount));

        page.Marking.ClearMarksCommand.Execute(null);

        Assert.Equal("None", page.Marking.MarkedFigure);
        Assert.All(page.Groups, row => Assert.False(row.HasMarks));

        Row(page, small.Files[1]).ToggleCommand.Execute(null);
        List<string?> told = [];
        page.Marking.PropertyChanged += (_, changed) => told.Add(changed.PropertyName);

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Contains(nameof(DuplicateMarkingViewModel.MarkedFigure), told);
        Assert.Equal("None", page.Marking.MarkedFigure);
    });

    /// <summary>
    /// A group is known by its file's name, and one whose copies are named differently says so, as a
    /// content match can join a photograph renamed on the way off a phone.
    /// </summary>
    [Fact]
    public void AGroupIsNamedByItsFileAndSaysWhereItsCopiesAreNamedDifferently() => UiThread.Run(async () =>
    {
        var renamed = Group(Copy(_scene.Folder("A"), "IMG_0001.jpg"), Copy(_scene.Folder("B"), "Beach.jpg"), Copy(_scene.Folder("C"), "img_0001.JPG"));
        var page = PageWithPhotos(Finds(_scene.Pair("same.png", 8192), renamed));

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["same.png", "IMG_0001.jpg and 1 other name"], page.Groups.Select(row => row.Name));
        Assert.All(page.Groups, row => Assert.Equal(FileKindGlyph.Of("photo.jpg"), row.Glyph));
        Assert.All(page.Groups, row => Assert.StartsWith(row.Name + ".", row.Description, StringComparison.Ordinal));
    });

    /// <summary>
    /// The settings stay open until a search starts and then fold, and their one line follows what
    /// they say: a location added and a criterion changed each show at once.
    /// </summary>
    [Fact]
    public void ASearchFoldsTheSettingsAndTheirLineFollowsThem() => UiThread.Run(async () =>
    {
        var page = Page(Finds());
        Assert.True(page.IsSetupOpen);
        Assert.StartsWith("No drive or folder chosen", page.SetupSummary, StringComparison.Ordinal);

        page.Locations.AddFolder(Photos);
        Assert.StartsWith(Photos, page.SetupSummary, StringComparison.Ordinal);

        page.Filters.MatchName = true;
        Assert.Equal(DuplicateSearchSummary.Of(page.Locations.Chosen, page.Filters.Current), page.SetupSummary);
        Assert.Contains("name", page.SetupSummary, StringComparison.Ordinal);

        await page.SearchCommand.ExecuteAsync(null);

        Assert.False(page.IsSetupOpen);
        Assert.True(page.IsIdle);
        Assert.True(page.HasNoGroups);
    });
}
