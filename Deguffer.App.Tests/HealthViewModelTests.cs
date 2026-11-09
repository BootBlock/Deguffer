using System.Collections.Specialized;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Health;
using Deguffer.Core.Working;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Health page wires Core's reading (§7.6): one read on the first arrival, rows kept in place,
/// a superseded read's reports ignored, and the offer following what was refused. What each reading
/// means is proved in Core.
/// </summary>
public sealed class HealthViewModelTests
{
    private static readonly DriveDescription First = new(new DriveKey("a"), 0, "First");
    private static readonly DriveDescription Second = new(new DriveKey("b"), 1, "Second");

    private readonly RunningActions _running = new();
    private readonly BusyWork _work = new();
    private readonly List<ElevationRequest> _relaunches = [];
    private IReadOnlyList<DriveDescription>? _drives = [First, Second];
    private int _reads;
    private int _lists;
    private Func<IReadOnlyList<DriveDescription>, IProgress<DriveHealth>, CancellationToken, Task> _reader = ReportAll();
    private IProgress<DriveVolumes>? _placing;

    private HealthViewModel Page(bool isElevated = false) => new(
        _ =>
        {
            Interlocked.Increment(ref _lists);
            return Task.FromResult(_drives);
        },
        (drives, progress, volumes, ct) =>
        {
            Interlocked.Increment(ref _reads);
            _placing = volumes;
            return _reader(drives, progress, ct);
        },
        isElevated,
        request =>
        {
            _relaunches.Add(request);
            return true;
        },
        _running,
        _work);

    private static Func<IReadOnlyList<DriveDescription>, IProgress<DriveHealth>, CancellationToken, Task> ReportAll(bool moreWhenElevated = false) =>
        (drives, progress, _) =>
        {
            foreach (var drive in drives)
            {
                progress.Report(Health(drive, moreWhenElevated: moreWhenElevated));
            }

            return Task.CompletedTask;
        };

    private static DriveHealth Health(DriveDescription drive, DriveVerdict verdict = DriveVerdict.Good, bool moreWhenElevated = false, string reading = "ok") =>
        new(
            drive.Key,
            drive.Name,
            verdict,
            "summary",
            "40 °C",
            [],
            [new DriveFact("Model", drive.Name)],
            [new HealthAttribute("temperature", "Temperature", reading, "meaning", AttributeStanding.Normal, "raw")],
            moreWhenElevated);

    [Fact]
    public void EnsureReadReadsOnceHoweverOftenThePageArrives() => UiThread.Run(async () =>
    {
        var page = Page();

        page.EnsureRead();
        await Eventually.HoldsAsync(() => !page.IsReading && page.Rows.Count == 2, "the first read");
        page.EnsureRead();
        page.EnsureRead();

        // A second read would be under way by now, so this waits for it to have asked the machine.
        await Eventually.HoldsAsync(() => !page.IsReading, "any further read to end");

        Assert.Equal(1, _lists);
        Assert.Equal(1, _reads);
    });

    [Fact]
    public void TheFirstDriveIsSelectedAsSoonAsItIsListed() => UiThread.Run(async () =>
    {
        using var hold = new SemaphoreSlim(0);
        _reader = async (_, _, ct) => await hold.WaitAsync(ct);
        var page = Page();

        var read = page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows.Count == 2, "the drives listed");

        Assert.Equal("First", page.SelectedDrive?.Title);
        Assert.Equal("Reading…", page.Rows[1].VerdictText);

        hold.Release();
        await read;
    });

    [Fact]
    public void ARefreshKeepsTheRowsAndTheSelectionWithNoReset() => UiThread.Run(async () =>
    {
        var page = Page();
        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows[1].Report is not null, "the reports");

        var kept = page.Rows[1];
        page.SelectedDrive = kept;
        var changes = CollectionChanges.Watch(page.Rows);
        var attributeChanges = CollectionChanges.Watch(kept.Attributes);

        _reader = (drives, progress, _) =>
        {
            foreach (var drive in drives)
            {
                progress.Report(Health(drive, reading: "new"));
            }

            return Task.CompletedTask;
        };

        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => kept.Attributes[0].Reading == "new", "the new reading");

        Assert.Same(kept, page.SelectedDrive);
        Assert.Same(kept, page.Rows[1]);
        Assert.Empty(changes);
        Assert.Equal([NotifyCollectionChangedAction.Replace], attributeChanges);
    });

    [Fact]
    public void ASupersededReadsReportsAreIgnored() => UiThread.Run(async () =>
    {
        IProgress<DriveHealth>? stale = null;
        using var hold = new SemaphoreSlim(0);

        _reader = async (_, progress, ct) =>
        {
            stale = progress;

            try
            {
                await hold.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The abandoned read keeps its progress object, as a real read that was mid-call does.
            }
        };

        var page = Page();
        var abandoned = page.RefreshAsync();
        await Eventually.HoldsAsync(() => stale is not null, "the first read to start");

        _reader = ReportAll();
        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows[0].Report is not null, "the newer reports");
        await abandoned;

        stale!.Report(Health(First, DriveVerdict.Failing));
        await Task.Delay(30);

        Assert.Equal(DriveVerdict.Good, page.Rows[0].Verdict);
    });

    [Fact]
    public void ADriveGoneFromTheListingLeavesAndTheNearestIsSelected() => UiThread.Run(async () =>
    {
        var page = Page();
        await page.RefreshAsync();
        page.SelectedDrive = page.Rows[1];

        _drives = [First];
        await page.RefreshAsync();

        Assert.Equal("First", Assert.Single(page.Rows).Title);
        Assert.Equal("First", page.SelectedDrive?.Title);
    });

    [Fact]
    public void AVolumeLineReachesItsOwnRow() => UiThread.Run(async () =>
    {
        var page = Page();
        await page.RefreshAsync();

        _placing!.Report(new DriveVolumes(Second.Key, "D: (Data)"));
        await Eventually.HoldsAsync(() => page.Rows[1].Volumes.Length > 0, "the volume line");

        Assert.Equal("D: (Data)", page.Rows[1].Volumes);
        Assert.Equal(string.Empty, page.Rows[0].Volumes);
    });

    [Fact]
    public void AFailedListingRemovesNothingAndSaysSo() => UiThread.Run(async () =>
    {
        var page = Page();
        await page.RefreshAsync();

        _drives = null;
        await page.RefreshAsync();

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal("Windows would not list this computer's drives.", page.Headline);
    });

    [Fact]
    public void AReadNeverCountsAsARunningAction() => UiThread.Run(async () =>
    {
        using var hold = new SemaphoreSlim(0);
        var seenDuringRead = false;
        _reader = async (_, _, ct) =>
        {
            seenDuringRead = _running.MayEndProcess;
            await hold.WaitAsync(ct);
        };

        var page = Page();
        var read = page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows.Count > 0 && _reads == 1, "the read to start");

        hold.Release();
        await read;

        Assert.True(seenDuringRead);
    });

    [Fact]
    public void TheOfferFollowsTheReportsAndReopensOnThisPage() => UiThread.Run(async () =>
    {
        _reader = ReportAll(moreWhenElevated: true);
        var page = Page();

        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.CanElevate, "the offer");

        page.ElevateCommand.Execute(null);
        Assert.Equal([ElevationRequest.Health], _relaunches);
    });

    [Fact]
    public void AnElevatedPageMakesNoOffer() => UiThread.Run(async () =>
    {
        _reader = ReportAll(moreWhenElevated: true);
        var page = Page(isElevated: true);

        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows[1].Report is not null, "the reports");

        Assert.False(page.CanElevate);
    });

    [Fact]
    public void WithNothingRefusedThereIsNoOffer() => UiThread.Run(async () =>
    {
        var page = Page();

        await page.RefreshAsync();
        await Eventually.HoldsAsync(() => page.Rows[1].Report is not null, "the reports");

        Assert.False(page.CanElevate);
        Assert.Equal("2 drives, no concerns reported.", page.Headline);
    });
}
