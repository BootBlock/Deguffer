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

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(groups);
            var marks = await HandOverAsync(candidates, marksMade, ct);
            finding.Report(candidates);

            // On a thread of the search's own, as the search confirms them.
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

        var page = PageWithPhotos(run);
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
    /// its figures are shown, with the number of groups it applied, and only counts reach the trace,
    /// never a file's name. The clock is held still, so the batches are exactly two: the first group,
    /// posted at once, and the rest, which wait for an interval that never passes until the end of the
    /// search posts them.
    /// </summary>
    [Fact]
    public void EachBatchAppliedIsOneSpanWithItsGroupCount() => UiThread.Run(async () =>
    {
        Clock = new ManualTimeProvider();

        DuplicateGroup[] groups = [.. Enumerable.Range(0, 200).Select(i => _scene.Pair($"{i}.jpg", 4096 * (1 + (i % 7))))];

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding(groups);
            var marks = await HandOverAsync(candidates, marksMade, ct);
            finding.Report(candidates);

            // The first group's batch is posted to the page's thread before this yields, so it runs
            // before the rest are reported.
            found.Report(marks.Judge(groups[0]));
            await Task.Yield();

            foreach (var group in groups.Skip(1))
            {
                found.Report(marks.Judge(group));
            }

            return new DuplicateSearchResult(candidates, groups, default, Stopped: false);
        };

        var page = PageWithPhotos(run);
        using var recorder = new ShellEventRecorder();

        await page.SearchCommand.ExecuteAsync(null);

        Assert.Equal(groups.Length, page.Groups.Count);
        Assert.Equal(["GroupsStart", "GroupsStop", "GroupsStart", "GroupsStop"], recorder.Names);
        Assert.Equal([1, groups.Length - 1], recorder.Events.Where(raised => raised.Name == "GroupsStop").Select(raised => raised.Numbers.Single()));
        Assert.DoesNotContain(recorder.Events.SelectMany(raised => raised.Payload), value => value is string);
    });
}
