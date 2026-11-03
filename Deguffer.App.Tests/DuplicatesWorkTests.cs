using Deguffer.App.ViewModels;
using Deguffer.Core.Duplicates;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What the Duplicates page tells the taskbar button about a search: the stage's own measure, as the
/// page's bar shows it, and an end however the search ends.
/// </summary>
public sealed class DuplicatesWorkTests : DuplicatesPageScene
{
    [Fact]
    public void ASearchSaysHowFarItsStageHasGotAndEndsWithIt() => UiThread.Run(async () =>
    {
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding();
            marksMade(_scene.Marks(candidates));
            progress.Report(new DuplicateSearchProgress(DuplicateSearchStage.ComparingContent, 1, Total: 4));
            reported.SetResult();
            await release.Task;

            return new DuplicateSearchResult(candidates, [], default, Stopped: false);
        };

        var page = PageWithPhotos(run);
        var searching = page.SearchCommand.ExecuteAsync(null);
        await reported.Task;
        await Eventually.HoldsAsync(() => _work.Current.SingleOrDefault()?.Fraction == 0.25, "the stage's measure arriving");

        Assert.Equal(25, page.StageProgress);

        release.SetResult();
        await searching;

        Assert.True(_work.IsIdle);
    });

    /// <summary>
    /// Identifying the files that may match is a stage of its own, named with its count, so a long
    /// one never reads as a scan stood still or as content being read.
    /// </summary>
    [Fact]
    public void ASearchIdentifyingItsFilesSaysSoAndHowFar() => UiThread.Run(async () =>
    {
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            var candidates = DuplicateScene.Finding();
            marksMade(_scene.Marks(candidates));
            progress.Report(new DuplicateSearchProgress(DuplicateSearchStage.Identifying, 1, Total: 4));
            reported.SetResult();
            await release.Task;

            return new DuplicateSearchResult(candidates, [], default, Stopped: false);
        };

        var page = PageWithPhotos(run);
        var searching = page.SearchCommand.ExecuteAsync(null);
        await reported.Task;
        await Eventually.HoldsAsync(() => _work.Current.SingleOrDefault()?.Fraction == 0.25, "the stage's measure arriving");

        Assert.Equal("Identifying the files that may match: 1 of 4", page.Stage);
        Assert.Equal(25, page.StageProgress);

        release.SetResult();
        await searching;
    });

    [Fact]
    public void AStoppedSearchEndsItsWork() => UiThread.Run(async () =>
    {
        RunDuplicateSearch run = async (search, marksMade, finding, found, progress, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);

            return new DuplicateSearchResult(DuplicateScene.Finding(), [], default, Stopped: true);
        };

        var page = PageWithPhotos(run);
        var searching = page.SearchCommand.ExecuteAsync(null);

        Assert.False(_work.IsIdle);

        page.StopCommand.Execute(null);
        await searching;

        Assert.True(_work.IsIdle);
    });
}
