using System.ComponentModel;
using Deguffer.App.ViewModels;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Duplicates page wires Core's marking and removal (§7.4): a rule marks what Core decides, a
/// mark Core refuses never shows as made, the confirmation shows Core's words, and no rule, mark or
/// removal works on groups whose locations changed, while the search is still adding groups, or
/// while another action reads them. What may be marked and what goes is proved in Core.
/// </summary>
public sealed class DuplicateMarkingViewModelTests : DuplicatesPageScene
{
    private static readonly DateTime Day = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private DuplicateCandidate Copy(string folder, string name, int day, LocationRole role = LocationRole.Search) =>
        _scene.Copy(Path.Combine(folder, name), role: role) with { Modified = Day.AddDays(day) };

    private static DuplicateGroup Group(params DuplicateCandidate[] copies) => DuplicateScene.Group(MatchCriteria.Content, copies);

    private static DuplicateCopyRow Row(DuplicatesViewModel page, DuplicateCandidate copy) =>
        page.Groups.SelectMany(group => group.Copies).Single(row => row.Copy.Identity == copy.Identity);

    private static void Toggle(DuplicatesViewModel page, DuplicateCandidate copy) => Row(page, copy).ToggleCommand.Execute(null);

    private static HashSet<FileIdentity> MarkedOn(DuplicatesViewModel page) =>
        [.. page.Groups.SelectMany(group => group.Copies).Where(row => row.IsMarked).Select(row => row.Copy.Identity)];

    /// <summary>Whether each rule, removal and mark the page offers is open.</summary>
    private static bool[] Open(DuplicatesViewModel page) =>
    [
        page.Marking.RunRuleCommand.CanExecute(null),
        page.Marking.MoveToRecycleBinCommand.CanExecute(null),
        page.Marking.DeletePermanentlyCommand.CanExecute(null),
        page.Groups[0].Copies[0].ToggleCommand.CanExecute(null),
    ];

    /// <summary>
    /// Each rule the page offers, run from the page, marks exactly what Core's rule marks over the
    /// same groups. The groups make every rule mark something different, so a rule handed over as
    /// another, or a folder rule handed another folder, shows.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EachRuleMarksWhatCoreDecided(int index) => UiThread.Run(async () =>
    {
        var camera = Directory.CreateDirectory(_scene.Folder("Camera")).FullName;
        DuplicateGroup[] groups =
        [
            Group(Copy(camera, "a.jpg", 2), Copy(Path.Combine(_scene.Folder("Documents"), "Trips"), "a.jpg", 3), Copy(_scene.Folder("D"), "a.jpg", 1)),
            Group(Copy(Path.Combine(_scene.Folder("Documents"), "A longer folder"), "b.jpg", 1), Copy(camera, "b.jpg", 2)),
        ];
        MarkingRule[] rules =
        [
            new MarkingRule.KeepNewest(),
            new MarkingRule.KeepOldest(),
            new MarkingRule.KeepShortestPath(),
            new MarkingRule.KeepInFolder(camera),
            new MarkingRule.MarkInFolder(camera),
        ];
        Assert.Equal(rules.Length, DuplicateMarkingViewModel.RuleNames.Count);

        var page = PageWithPhotos(Finds(groups));
        await page.SearchCommand.ExecuteAsync(null);
        page.Marking.RuleIndex = index;
        page.Marking.RuleFolder = camera;
        await page.Marking.RunRuleCommand.ExecuteAsync(null);

        // Core's own answer, over marks made as the search makes them.
        var core = _scene.Marks(DuplicateScene.Finding(groups));

        foreach (var group in groups)
        {
            core.Add(group);
        }

        core.Complete();
        var outcome = core.Run(rules[index]);
        HashSet<FileIdentity> marked = [.. core.Groups.SelectMany(group => group.Group.Files.Where(group.IsMarked)).Select(copy => copy.Identity)];

        Assert.NotEmpty(marked);
        Assert.Equal(marked, MarkedOn(page));
        Assert.Equal(outcome.Summary, page.Marking.Outcome);
    });

    /// <summary>
    /// A mark Core refuses is never shown as made: the check box is told the copy's state again, and
    /// the row says Core's reason, where pointer, keyboard and screen reader all reach it. A reference
    /// copy says why it is never marked before anyone tries.
    /// </summary>
    [Fact]
    public void ThePageOffersNoWayPastACoreRefusal() => UiThread.Run(async () =>
    {
        var first = Copy(_scene.Folder("Documents"), "a.jpg", 1);
        var last = Copy(_scene.Folder("Downloads"), "a.jpg", 1);
        var reference = Copy(Photos, "b.jpg", 1, LocationRole.Reference);
        var page = PageWithPhotos(Finds(Group(first, last), Group(Copy(_scene.Folder("Documents"), "b.jpg", 1), reference)));
        await page.SearchCommand.ExecuteAsync(null);

        Toggle(page, first);
        Assert.True(Row(page, first).IsMarked);

        var lastRow = Row(page, last);
        List<string?> told = [];
        ((INotifyPropertyChanged)lastRow).PropertyChanged += (_, changed) => told.Add(changed.PropertyName);
        Toggle(page, last);

        Assert.False(lastRow.IsMarked);
        Assert.False(lastRow.Group.IsMarked(last));
        Assert.Contains(nameof(DuplicateCopyRow.IsMarked), told);
        Assert.Equal("Marking this would leave no copy in the group that can be kept, and every group keeps one.", lastRow.Note);
        Assert.Contains(lastRow.Note, lastRow.Description, StringComparison.Ordinal);

        var referenceRow = Row(page, reference);
        var never = CopyRefusals.WhyNeverMarked(reference)!;
        Assert.Equal(never, referenceRow.WhyNotMarked);
        Assert.Contains(never, referenceRow.Description, StringComparison.Ordinal);

        Toggle(page, reference);

        Assert.False(referenceRow.IsMarked);
        Assert.Equal(never, referenceRow.Note);
    });

    /// <summary>
    /// The confirmation the page asks with is Core's, word for word, in the way the button chosen
    /// says: the Recycle Bin by default, and permanent removal only from its own button.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void TheConfirmationShowsCoresWords(ExploreRemovalMode mode) => UiThread.Run(async () =>
    {
        var group = _scene.Pair("a.jpg", 4096);
        var page = PageWithPhotos(Finds(group));
        await page.SearchCommand.ExecuteAsync(null);
        Toggle(page, group.Files[1]);

        await (mode == ExploreRemovalMode.RecycleBin ? page.Marking.MoveToRecycleBinCommand : page.Marking.DeletePermanentlyCommand)
            .ExecuteAsync(null);

        var asked = Assert.Single(Prompt.Asked);
        var core = await RemovalConfirmation.ForAsync(page.Marks!, await _scene.ProtectionsAsync(), mode, _ => null);

        Assert.Equal(mode, asked.Mode);
        Assert.Equal([group.Files[1]], asked.Copies);
        Assert.Equal(core.Title, asked.Title);
        Assert.Equal(core.Summary, asked.Summary);
        Assert.Equal(core.Warnings, asked.Warnings);
        Assert.Equal(core.ConfirmLabel, asked.ConfirmLabel);
        Assert.Equal("Nothing was removed.", page.Marking.Outcome);
    });

    public static TheoryData<string> LocationChanges => ["role", "added", "taken away"];

    /// <summary>
    /// Once the locations change after a search, its groups show copies in roles the user no longer
    /// chose, so every rule, mark and removal stops, even invoked without asking, and says Core's
    /// reason; a search in the new roles opens them again.
    /// </summary>
    [Theory]
    [MemberData(nameof(LocationChanges))]
    public void ALocationChangedAfterASearchStopsEveryRuleAndRemovalUntilTheNextSearch(string change) => UiThread.Run(async () =>
    {
        // Three copies, so with one marked a rule or a hand mark would still have another to mark.
        var group = Group(Copy(_scene.Folder("Documents"), "a.jpg", 1), Copy(_scene.Folder("Downloads"), "a.jpg", 1), Copy(_scene.Folder("Pictures"), "a.jpg", 1));
        var page = PageWithPhotos(Finds(group));
        page.Locations.AddFolder(_scene.Folder("Backup"));
        await page.SearchCommand.ExecuteAsync(null);
        Toggle(page, group.Files[1]);
        Assert.All(Open(page), Assert.True);

        switch (change)
        {
            case "role":
                page.Locations.Rows[1].IsReference = true;
                break;
            case "added":
                page.Locations.AddFolder(_scene.Folder("Music"));
                break;
            default:
                page.Locations.Rows[1].RemoveCommand.Execute(null);
                break;
        }

        Assert.All(Open(page), Assert.False);
        Assert.False(page.Marking.ClearMarksCommand.CanExecute(null));
        Assert.Equal(_searches[^1].WhyResultsDoNotApply(page.Locations.Chosen), page.Marking.WhyClosed);

        // Invoked anyway, as an automation client can.
        await page.Marking.RunRuleCommand.ExecuteAsync(null);
        await page.Marking.MoveToRecycleBinCommand.ExecuteAsync(null);
        await page.Marking.DeletePermanentlyCommand.ExecuteAsync(null);
        page.Marking.ClearMarksCommand.Execute(null);
        Toggle(page, group.Files[0]);

        Assert.Empty(Prompt.Asked);
        Assert.Equal([group.Files[1].Identity], MarkedOn(page));

        // A new search starts with nothing marked, so a removal waits for a mark as well.
        await page.SearchCommand.ExecuteAsync(null);
        Toggle(page, group.Files[1]);

        Assert.All(Open(page), Assert.True);
    });

    /// <summary>
    /// While the search still adds groups on the page's thread, no rule, confirmation or removal reads
    /// them on another, even invoked without asking. A mark by hand, made on the page's thread, stays
    /// open. Once the search ends, all of them open.
    /// </summary>
    [Fact]
    public void NoConfirmationOrRemovalCanStartWhileASearchRuns() => UiThread.Run(async () =>
    {
        var group = _scene.Pair("a.jpg", 4096);
        var ended = new TaskCompletionSource();

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            _searches.Add(search);
            var candidates = DuplicateScene.Finding(group);
            await Task.Run(() => marksMade(_scene.Marks(candidates)), ct);
            finding.Report(candidates);
            found.Report(group);
            await ended.Task;

            return new DuplicateSearchResult(candidates, [group], default, Stopped: false);
        };

        var page = PageWithPhotos(run);
        var searching = page.SearchCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => page.Groups.Count == 1, "the group arriving");

        Toggle(page, group.Files[1]);
        Assert.True(Row(page, group.Files[1]).IsMarked);

        Assert.False(page.Marking.RunRuleCommand.CanExecute(null));
        Assert.False(page.Marking.MoveToRecycleBinCommand.CanExecute(null));
        Assert.False(page.Marking.DeletePermanentlyCommand.CanExecute(null));
        Assert.NotEmpty(page.Marking.WhyClosed);

        await page.Marking.RunRuleCommand.ExecuteAsync(null);
        await page.Marking.MoveToRecycleBinCommand.ExecuteAsync(null);
        await page.Marking.DeletePermanentlyCommand.ExecuteAsync(null);

        Assert.Empty(Prompt.Asked);

        ended.SetResult();
        await searching;

        Assert.All(Open(page), Assert.True);
    });

    /// <summary>
    /// While a removal is being asked about or carried out, nothing changes the marks it reads on
    /// another thread, and no search takes its groups from under it.
    /// </summary>
    [Fact]
    public void WhileARemovalRunsNoMarkChangesAndNoSearchStarts() => UiThread.Run(async () =>
    {
        var group = _scene.Pair("a.jpg", 4096);
        var answer = new TaskCompletionSource<bool>();
        Prompt = new FakeDuplicateConfirmation(answer.Task);
        var page = PageWithPhotos(Finds(group));
        await page.SearchCommand.ExecuteAsync(null);
        Toggle(page, group.Files[1]);

        var removing = page.Marking.MoveToRecycleBinCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => Prompt.Asked.Count == 1, "the confirmation");

        Assert.True(page.Marking.IsBusy);
        Assert.All(Open(page), Assert.False);
        Assert.False(page.Marking.ClearMarksCommand.CanExecute(null));
        Assert.False(page.SearchCommand.CanExecute(null));

        Toggle(page, group.Files[1]);
        page.Marking.ClearMarksCommand.Execute(null);
        Assert.Equal([group.Files[1].Identity], MarkedOn(page));

        answer.SetResult(false);
        await removing;

        Assert.All(Open(page), Assert.True);
        Assert.True(page.SearchCommand.CanExecute(null));
    });

    /// <summary>
    /// A confirmed removal moves the marked copy to the Recycle Bin and leaves the copy kept, each row
    /// says what became of its copy, and the groups, which now describe the disk as it was, take no
    /// more marks until the next search.
    /// </summary>
    [Fact]
    public void AConfirmedRemovalMovesTheMarkedCopyAndKeepsTheOther() => UiThread.Run(async () =>
    {
        var content = new byte[200 * 1024];
        new Random(297).NextBytes(content);
        var kept = _scene.Written(Path.Combine(_scene.Folder("Documents"), "a.bin"), content);
        var copy = _scene.Written(Path.Combine(_scene.Folder("Downloads"), "a.bin"), content);
        Prompt = new FakeDuplicateConfirmation(true);
        var page = PageWithPhotos(Finds(Group(kept, copy)));
        await page.SearchCommand.ExecuteAsync(null);
        Toggle(page, copy);

        await page.Marking.MoveToRecycleBinCommand.ExecuteAsync(null);

        Assert.False(File.Exists(copy.Path));
        Assert.True(File.Exists(kept.Path));
        Assert.Single(Directory.GetFiles(_scene.Bin));
        Assert.Equal("Moved to the Recycle Bin.", Row(page, copy).Note);
        Assert.StartsWith("Moved 1 copy", page.Marking.Outcome, StringComparison.Ordinal);
        Assert.True(_running.MayEndProcess);
        Assert.All(Open(page), Assert.False);
        Assert.NotEmpty(page.Marking.WhyClosed);
    });
}
