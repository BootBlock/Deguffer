using System.Collections.Specialized;
using Deguffer.App.ViewModels;
using Deguffer.Core.Duplicates;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the groups a search confirms reach the Duplicates page: judged already on the search's
/// thread, taken in batches rather than one post each, and placed where Core's marks put them, so a
/// search that confirms thousands of groups at once neither floods the page's thread nor moves a row.
/// </summary>
public sealed class DuplicatesArrivalTests : DuplicatesPageScene
{
    /// <summary>
    /// A search with no content criterion confirms every group in one loop. The page takes them in at
    /// most two turns of its thread (the one the first group posted, and the one the end of the search
    /// posts for the rest), never one each, and in the order <see cref="DuplicateMarks.Add"/> gives.
    /// The clock is held still, so no batch reaches its budget or its interval.
    /// </summary>
    [Fact]
    public void AThousandGroupsConfirmedAtOnceArriveInATurnOrTwoInTheOrderTheMarksGive() => UiThread.Run(async () =>
    {
        Clock = new ManualTimeProvider();

        // Sizes that repeat, so ties have to keep the order they arrived in.
        DuplicateGroup[] groups = [.. Enumerable.Range(0, 1000).Select(i => _scene.Pair($"{i}.jpg", 4096 * (1 + (i % 7))))];

        var page = PageWithPhotos(ConfirmingAllAtOnce(groups));
        HashSet<int> turns = [];
        page.Groups.CollectionChanged += (_, changed) =>
        {
            if (changed.Action == NotifyCollectionChangedAction.Add)
            {
                turns.Add(UiThread.Item);
            }
        };

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(groups.Length, page.Groups.Count);
        Assert.InRange(turns.Count, 1, 2);
        Assert.Equal(page.Marks!.Groups, page.Groups.Select(row => row.Marks));
        Assert.Equal(groups.Length, page.Marks.Groups.Count);
    });

    /// <summary>
    /// Each batch the page applies is one span in a trace, opened by its first group and closed once
    /// its figures are shown, with the number of groups it applied: every group confirmed is in one
    /// batch's count, and only counts reach the trace, never a file's name.
    /// </summary>
    [Fact]
    public void EachBatchAppliedIsOneSpanWithItsGroupCount() => UiThread.Run(async () =>
    {
        Clock = new ManualTimeProvider();

        DuplicateGroup[] groups = [.. Enumerable.Range(0, 200).Select(i => _scene.Pair($"{i}.jpg", 4096 * (1 + (i % 7))))];
        var page = PageWithPhotos(ConfirmingAllAtOnce(groups));
        using var recorder = new ShellEventRecorder();

        await page.SearchCommand.ExecuteAsync(null);

        var batches = recorder.Events.Count / 2;

        Assert.InRange(batches, 1, 2);
        Assert.Equal(Enumerable.Repeat<string[]>(["GroupsStart", "GroupsStop"], batches).SelectMany(pair => pair), recorder.Names);
        Assert.Equal(groups.Length, recorder.Events.Where(raised => raised.Name == "GroupsStop").Sum(raised => raised.Numbers.Single()));
        Assert.DoesNotContain(recorder.Events.SelectMany(raised => raised.Payload), value => value is string);
    });

    /// <summary>
    /// A search that finds <paramref name="groups"/> and confirms every one of them in one loop on a
    /// thread of its own, as a search with no content criterion does.
    /// </summary>
    private RunDuplicateSearch ConfirmingAllAtOnce(DuplicateGroup[] groups) =>
        async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(groups);
            var marks = await HandOverAsync(candidates, marksMade, ct);
            finding.Report(candidates);

            await Task.Run(
                () =>
                {
                    foreach (var group in groups)
                    {
                        found.Report(marks.Judge(group));
                    }
                },
                ct);

            return new DuplicateSearchResult(candidates, groups, default, Stopped: false);
        };
}
