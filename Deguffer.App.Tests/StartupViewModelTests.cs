using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Memory;
using Deguffer.Core.Startup;
using Deguffer.Core.Working;
using Deguffer.Testing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Startup page wires Core's decisions (§7.5): rows follow a reading in place with what each
/// program holds, nothing is picked for the user, a change goes through the switcher and reads again
/// after, a refusal is a sentence, and a link from Memory picks its entry. What may change and what a
/// change writes is proved in Core.
/// </summary>
public sealed class StartupViewModelTests : IDisposable
{
    private const string Tool = @"C:\Program Files\Tool\tool.exe";

    private readonly TempDirectory _temp = new();
    private readonly FakeStartupRegistry _registry = new();
    private readonly FakeProgramFiles _programs = new FakeProgramFiles().With(Tool, description: "Tool Tray", company: "Contoso");
    private readonly RunningActions _running = new();
    private readonly BusyWork _work = new();
    private readonly List<ElevationRequest> _relaunches = [];
    private readonly FakeUserEnvironment _user;
    private readonly FakeSystemDirectories _system;
    private ProcessMemoryTable _processes = new([], ProcessFigures.Checked, Complete: true);
    private int _switches;

    public StartupViewModelTests()
    {
        _user = new FakeUserEnvironment(_temp.CreateDirectory("user"));
        _system = new FakeSystemDirectories(_temp.CreateDirectory("system"));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AReadingFillsTheRowsWithWhatEachProgramHoldsAndPicksNothing() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        _processes = new ProcessMemoryTable(
            [new ProcessMemory(10, 4, "tool.exe", 1, 3L << 20, 3L << 20, 1)], ProcessFigures.Checked, Complete: true);
        var page = Page();

        await page.RefreshAsync();

        var row = Assert.Single(page.Rows);
        Assert.Equal("Tool Tray", row.Title);
        Assert.Equal("ToolTray · Contoso · Registry, this user", row.Detail);
        Assert.Equal("Starts", row.State);
        Assert.Equal("3.0 MB", row.Memory);
        Assert.True(row.HasMemory);
        Assert.False(row.IsNotRunning);
        Assert.StartsWith("1 entry: 1 starts at sign-in", page.Headline, StringComparison.Ordinal);

        // §7.5: nothing is picked for the user.
        Assert.Null(page.SelectedRow);
        Assert.Empty(page.Note);
    });

    /// <summary>A live list is updated in place, never rebuilt: the picked row stays picked across the reading a change ends with.</summary>
    [Fact]
    public void AChangeGoesThroughTheSwitcherAndTheRowFollowsInPlace() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        var page = Page();
        await page.RefreshAsync();
        var row = page.Rows[0];
        page.SelectedRow = row;

        Assert.Equal("Disable", page.ChangeLabel);

        await page.ChangeCommand.ExecuteAsync(null);

        Assert.Equal(1, _switches);
        Assert.Equal(InfoBarSeverity.Success, page.ReportSeverity);
        Assert.True(page.HasReportChecks);
        Assert.Contains(page.ReportChecks, c => c.Outcome is VerificationOutcome.Sent);
        Assert.Same(row, Assert.Single(page.Rows));
        Assert.Equal("Disabled", row.State);
        Assert.Equal("Enable", page.ChangeLabel);
        Assert.StartsWith("Enable records", page.Note, StringComparison.Ordinal);
        Assert.True(_work.IsIdle);
        Assert.True(_running.MayEndProcess);
    });

    /// <summary>A refusal is a sentence, never a disabled button, and nothing reaches the switcher.</summary>
    [Fact]
    public void ARefusedChangeSaysWhyAndChangesNothing() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.MachineRun, "ToolTray", Tool);
        var page = Page();
        await page.RefreshAsync();
        page.SelectedRow = page.Rows[0];

        Assert.True(page.ChangeCommand.CanExecute(null));
        Assert.Contains("administrator", page.Note, StringComparison.Ordinal);
        Assert.True(page.Rows[0].HasShield);

        await page.ChangeCommand.ExecuteAsync(null);

        Assert.Equal(0, _switches);
        Assert.Empty(_registry.Writes);
        Assert.Equal(InfoBarSeverity.Informational, page.ReportSeverity);
    });

    [Fact]
    public void ALinkFromMemoryPicksItsEntryOnceAReadingHoldsIt() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool).WithRun(StartupSource.UserRun, "Other", Tool);
        var page = Page();

        page.Reveal(new StartupKey(StartupSource.UserRun, "Other"));
        Assert.Null(page.SelectedRow);

        await page.RefreshAsync();

        Assert.Equal("Other", page.SelectedRow?.Key.Name);
    });

    /// <summary>
    /// A link that has not landed must never move a pick the user made by hand afterwards (§7.5), even
    /// when a later reading holds the entry it named.
    /// </summary>
    [Fact]
    public void APickByHandEndsALinksWait() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        var page = Page();
        await page.RefreshAsync();

        page.Reveal(new StartupKey(StartupSource.UserRun, "Later"));
        await Eventually.HoldsAsync(() => !page.IsReading, "the read the link asked for");
        page.SelectedRow = page.Rows[0];
        _registry.WithRun(StartupSource.UserRun, "Later", Tool);

        await page.RefreshAsync();

        Assert.Equal("ToolTray", page.SelectedRow?.Key.Name);
    });

    /// <summary>A link from a newer reading than the page's reads the list again, so an entry added since is found.</summary>
    [Fact]
    public void ALinkToAnEntryTheListLacksReadsTheListAgain() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        var page = Page();
        await page.RefreshAsync();
        _registry.WithRun(StartupSource.UserRun, "Later", Tool);

        page.Reveal(new StartupKey(StartupSource.UserRun, "Later"));
        await Eventually.HoldsAsync(() => page.SelectedRow is not null, "the entry picked");

        Assert.Equal("Later", page.SelectedRow!.Key.Name);
    });

    [Fact]
    public void ALinkToAnEntryAlreadyListedPicksItAtOnce() => UiThread.Run(async () =>
    {
        _registry.WithRun(StartupSource.UserRun, "ToolTray", Tool);
        var page = Page();
        await page.RefreshAsync();

        page.Reveal(new StartupKey(StartupSource.UserRun, "TOOLTRAY"));

        Assert.Same(page.Rows[0], page.SelectedRow);
    });

    [Fact]
    public void ReopeningElevatedAsksForThisPage()
    {
        var page = Page();

        page.ElevateCommand.Execute(null);

        Assert.Equal([ElevationRequest.Startup], _relaunches);
    }

    private StartupViewModel Page()
    {
        var folders = new StartupFolderReader(_user, _system);
        var reader = new StartupReader(_registry, folders, new FakeShortcutReader(), _programs, FakePackagedStartupTasks.None);
        var switcher = new StartupSwitcher(_registry, folders, new ManualTimeProvider());

        return new StartupViewModel(
            ct => StartupView.Read(reader, new TableSource(_processes), new Images(Tool), ct),
            entry =>
            {
                _switches++;
                return switcher.Switch(entry, isElevated: false);
            },
            isElevated: false,
            request =>
            {
                _relaunches.Add(request);
                return false;
            },
            () => Task.FromResult(true),
            _running,
            _work);
    }

    /// <summary>A process table a test wrote, with the rest of the snapshot the fake source gives.</summary>
    private sealed class TableSource(ProcessMemoryTable table) : IMemorySource
    {
        public MemorySnapshot Read(CancellationToken ct) => FakeMemorySource.Snapshot with { Processes = table };
    }

    /// <summary>Every process asked about runs <paramref name="image"/>.</summary>
    private sealed class Images(string image) : IProcessImages
    {
        public ProcessImage ImageOf(int processId, long creationTime) => ProcessImage.Of(image);
    }
}
