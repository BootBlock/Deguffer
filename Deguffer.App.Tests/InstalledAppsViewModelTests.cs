using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Installed apps page wires Core's decisions (§7.3): rows follow a new reading in place, a
/// removal asks first and reads again after, the backup switch is remembered, and the elevated
/// reopen asks for this page. What is stale and what may be removed is proved in Core.
/// </summary>
public sealed class InstalledAppsViewModelTests : IDisposable
{
    private const string RegExe = @"C:\Windows\System32\reg.exe";

    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakeWindowsInstaller _installer = new();

    private readonly FakePathProbe _paths = new();

    /// <summary>A file the probe reports standing, named under the test's tree.</summary>
    private string StandingFile(string folder, string name)
    {
        var path = Path.Combine(_temp.Path, folder, name);
        _paths.File(path);
        return path;
    }

    private readonly FakeProcessRunner _runner = new();

    private readonly ScriptedInstalledAppsPrompt _prompt = new(answer: true);

    private readonly PreferenceService _preferences;

    private readonly List<ElevationRequest> _relaunches = [];

    private readonly RunningActions _running = new();

    private readonly FakeUninstallLauncher _launcher = new();

    public InstalledAppsViewModelTests() =>
        _preferences = new PreferenceService(new PreferenceStore(new FakeUserEnvironment(_temp.Path)));

    public void Dispose() => _temp.Dispose();

    private InstalledAppsViewModel Page(bool relaunchStarts = false)
    {
        var reader = new InstalledAppsReader(_registry, _installer, _paths, FixedSystemDirectories.Standard, new FakePackageDependencies(), new FakeVolumeInventory());
        var backups = new RegistryBackups(_runner, Path.Combine(_temp.Path, "backups"), RegExe, TimeProvider.System);

        return new InstalledAppsViewModel(
            reader.Read,
            new InstalledAppsActions(
                new EntryRemover(_registry, reader, backups),
                new BackupRestorer(_registry, backups),
                new ProgramUninstaller(reader, _launcher, @"C:\Windows\System32\msiexec.exe"),
                backups,
                () => _prompt,
                _preferences,
                isElevated: false,
                _running),
            isElevated: false,
            request =>
            {
                _relaunches.Add(request);
                return relaunchStarts;
            },
            _running);
    }

    private UninstallKey Stale(string name) => _registry.With(UninstallScope.CurrentUser, name,
        ("DisplayName", name), ("UninstallString", $"\"{Path.Combine(_temp.Path, name, "unins000.exe")}\""));

    private UninstallKey Installed(string name) => _registry.With(UninstallScope.CurrentUser, name,
        ("DisplayName", name), ("UninstallString", $"\"{StandingFile(name, "unins000.exe")}\""));

    [Fact]
    public void AReadingFillsBothListsAndTheHeadline() => UiThread.Run(async () =>
    {
        Stale("Gone");
        Installed("Tool");
        var page = Page();

        await page.RefreshAsync();

        Assert.Equal(["Gone"], page.StaleRows.Select(r => r.Name));
        Assert.Equal(["Tool"], page.InstalledRows.Select(r => r.Name));
        Assert.StartsWith("1 stale entry and 1 installed program", page.Headline, StringComparison.Ordinal);
    });

    /// <summary>A live list is updated in place, never rebuilt: a row that stays keeps its identity.</summary>
    [Fact]
    public void ARowThatStaysKeepsItsIdentityAcrossReadings() => UiThread.Run(async () =>
    {
        Stale("A");
        var gone = Stale("B");
        var page = Page();
        await page.RefreshAsync();
        var kept = page.StaleRows[0];

        _registry.Remove(gone);
        await page.RefreshAsync();

        Assert.Same(kept, Assert.Single(page.StaleRows));
    });

    [Fact]
    public void TheFilterNarrowsTheListsWithoutReadingAgain() => UiThread.Run(async () =>
    {
        Installed("Alpha");
        Installed("Beta");
        var page = Page();
        await page.RefreshAsync();
        var reads = _registry.Reads[UninstallScope.CurrentUser];

        page.Filter = "alp";

        Assert.Equal(["Alpha"], page.InstalledRows.Select(r => r.Name));
        Assert.Equal(reads, _registry.Reads[UninstallScope.CurrentUser]);
    });

    [Fact]
    public void ARemovalAsksFirstThenReadsTheListAgain() => UiThread.Run(async () =>
    {
        var gone = Stale("Gone");
        var page = Page();
        await page.RefreshAsync();
        page.Actions.BackUpFirst = false;

        page.Actions.SelectStale([page.StaleRows[0].Entry]);
        await page.Actions.RemoveCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => page.StaleRows.Count == 0, "the removed entry leaving the list");

        Assert.Equal(1, _prompt.Asked);
        Assert.Equal([gone], _registry.Deleted);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, page.Actions.ReportSeverity);
    });

    [Fact]
    public void DecliningRemovesNothing() => UiThread.Run(async () =>
    {
        Stale("Gone");
        var page = Page();
        await page.RefreshAsync();
        _prompt.Answer = false;

        page.Actions.SelectStale([page.StaleRows[0].Entry]);
        await page.Actions.RemoveCommand.ExecuteAsync(null);

        Assert.Empty(_registry.Deleted);
        Assert.Equal("Nothing was removed.", page.Actions.Report);
    });

    [Fact]
    public void ARestoredBackupSaysItsEntryIsBack() => UiThread.Run(async () =>
    {
        var key = new UninstallKey(UninstallScope.CurrentUser, "Tool");
        _runner.Replying(RegExe, _ =>
        {
            _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));
            return null;
        });
        var file = Path.Combine(_temp.CreateDirectory("backups"), "tool.reg");
        File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", "", $"[{key.PhysicalPath}]", "\"DisplayName\"=\"Tool\""], System.Text.Encoding.Unicode);
        var page = Page();
        page.Actions.SelectedBackup = BackupRow.For(new RegistryBackupFile(file, key.PhysicalPath, "Tool", DateTimeOffset.UnixEpoch, IsConfined: true));

        await page.Actions.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("Restored. The entry is back in the list.", page.Actions.Report);
        Assert.Contains("already in the list", page.Actions.RestoreNote, StringComparison.Ordinal);
    });

    [Fact]
    public void TheBackupSwitchIsRemembered()
    {
        var page = Page();

        page.Actions.BackUpFirst = false;

        Assert.False(_preferences.Current.BackUpInstalledAppEntries);
        Assert.False(new PreferenceStore(new FakeUserEnvironment(_temp.Path)).Load().BackUpInstalledAppEntries);
    }

    [Fact]
    public void AMachineWideSelectionSaysWhyItWillBeLeft() => UiThread.Run(async () =>
    {
        _registry.With(UninstallScope.Machine64, "Gone", ("DisplayName", "Gone"),
            ("UninstallString", $"\"{Path.Combine(_temp.Path, "Gone", "unins000.exe")}\""));
        var page = Page();
        await page.RefreshAsync();

        page.Actions.SelectStale([page.StaleRows[0].Entry]);

        Assert.Contains("administrator", page.Actions.RemovalNote, StringComparison.Ordinal);
    });

    [Fact]
    public void RowsWearTheMarksCoreGivesThem() => UiThread.Run(async () =>
    {
        _registry.With(UninstallScope.Machine64, "Gone", ("DisplayName", "Gone"),
            ("UninstallString", $"\"{Path.Combine(_temp.Path, "Gone", "unins000.exe")}\""));
        _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"), ("EstimatedSize", 2048),
            ("UninstallString", $"\"{StandingFile("Tool", "unins000.exe")}\""));
        var page = Page();

        await page.RefreshAsync();

        var stale = Assert.Single(page.StaleRows);
        Assert.True(stale.HasShield);
        Assert.Equal("Gone", stale.Standing);
        Assert.False(stale.HasSize);

        var installed = Assert.Single(page.InstalledRows);
        Assert.False(installed.HasShield);
        Assert.Equal("2.0 MB", installed.Size);
        Assert.Contains(installed.Reason, installed.Description, StringComparison.Ordinal);
    });

    [Fact]
    public void CheckedProgramsAreUninstalledInTurnThenTheListIsReadAgain() => UiThread.Run(async () =>
    {
        var a = Installed("A");
        var b = Installed("B");
        _launcher.WhileRunning = launch => _registry.Remove(launch.FileName.Contains($"{Path.DirectorySeparatorChar}A{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? a : b);
        var page = Page();
        await page.RefreshAsync();

        page.Actions.SelectInstalled([.. page.InstalledRows.Select(r => r.Entry)]);
        await page.Actions.UninstallCommand.ExecuteAsync(null);
        await Eventually.HoldsAsync(() => page.InstalledRows.Count == 0, "the uninstalled programs leaving the list");

        Assert.Equal(["Uninstall 2 programs?", "Uninstall 'B' next?"], _prompt.Prompts.Select(p => p.Title));
        Assert.Equal(2, _launcher.Started.Count);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, page.Actions.ReportSeverity);
        Assert.False(page.Actions.IsWatching);
    });

    [Fact]
    public void DecliningTheUninstallRunsNothing() => UiThread.Run(async () =>
    {
        Installed("A");
        var page = Page();
        await page.RefreshAsync();
        _prompt.Answer = false;

        page.Actions.SelectInstalled([page.InstalledRows[0].Entry]);
        await page.Actions.UninstallCommand.ExecuteAsync(null);

        Assert.Empty(_launcher.Started);
        Assert.Equal("Nothing was run.", page.Actions.Report);
    });

    [Fact]
    public void CancelStopsWatchingAndStartsNoMore() => UiThread.Run(async () =>
    {
        Installed("A");
        Installed("B");
        var page = Page();
        await page.RefreshAsync();
        _launcher.RunsUntilCancelled = true;
        _launcher.WhileRunning = _ => page.Actions.StopWatchingCommand.Execute(null);

        page.Actions.SelectInstalled([.. page.InstalledRows.Select(r => r.Entry)]);
        await page.Actions.UninstallCommand.ExecuteAsync(null);

        Assert.Single(_launcher.Started);
        Assert.Contains("It may still be running.", page.Actions.Report, StringComparison.Ordinal);
        Assert.Contains("'B' was not started", page.Actions.Report, StringComparison.Ordinal);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, page.Actions.ReportSeverity);
    });

    [Fact]
    public void ElevatingAsksToReopenOnThisPage()
    {
        var page = Page(relaunchStarts: false);
        var replaced = 0;
        page.ReplacedByElevatedInstance += (_, _) => replaced++;

        page.ElevateCommand.Execute(null);

        Assert.IsType<InstalledAppsRequest>(Assert.Single(_relaunches));
        Assert.Equal(0, replaced);
    }

    [Fact]
    public void AnElevatedReplacementThatStartedClosesThisOne()
    {
        var page = Page(relaunchStarts: true);
        var replaced = 0;
        page.ReplacedByElevatedInstance += (_, _) => replaced++;

        page.ElevateCommand.Execute(null);

        Assert.Equal(1, replaced);
    }

    /// <summary>
    /// Elevating ends this process, and a clean still running on the Storage page would end with
    /// it, unverified and unreported. The button waits for it, and comes back when it ends.
    /// </summary>
    [Fact]
    public void ElevatingWaitsForAnActionRunningOnAnotherPage()
    {
        var page = Page();
        var raised = 0;
        page.ElevateCommand.CanExecuteChanged += (_, _) => raised++;

        var clean = _running.Begin(RunningAction.StorageClean);

        Assert.False(page.ElevateCommand.CanExecute(null));

        clean.Dispose();

        Assert.True(page.ElevateCommand.CanExecute(null));
        Assert.Equal(2, raised);
    }

    /// <summary>
    /// Each action on this page is recorded as running until its report is up, so neither the
    /// window nor an Elevate button ends the process under it.
    /// </summary>
    [Fact]
    public void ARemovalIsRecordedAsRunningUntilItHasReported() => UiThread.Run(async () =>
    {
        Stale("Gone");
        var page = Page();
        await page.RefreshAsync();
        page.Actions.BackUpFirst = false;
        page.Actions.SelectStale([page.StaleRows[0].Entry]);
        var seen = new List<IReadOnlyList<RunningAction>>();
        _running.Changed += (_, _) => seen.Add(_running.Current);
        var mayEndAsReported = new List<bool>();
        page.Actions.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(page.Actions.Report))
            {
                mayEndAsReported.Add(_running.MayEndProcess);
            }
        };

        await page.Actions.RemoveCommand.ExecuteAsync(null);

        Assert.Equal([[RunningAction.EntryRemoval], []], seen);
        Assert.Equal([false], mayEndAsReported);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success, page.Actions.ReportSeverity);
    });

    [Fact]
    public void AnUninstallIsRecordedAsRunningWhileItsUninstallerRuns() => UiThread.Run(async () =>
    {
        var a = Installed("A");
        IReadOnlyList<RunningAction> whileRunning = [];
        _launcher.WhileRunning = _ =>
        {
            whileRunning = _running.Current;
            _registry.Remove(a);
        };
        var page = Page();
        await page.RefreshAsync();
        page.Actions.SelectInstalled([.. page.InstalledRows.Select(r => r.Entry)]);

        await page.Actions.UninstallCommand.ExecuteAsync(null);

        Assert.Equal([RunningAction.Uninstall], whileRunning);
        Assert.True(_running.MayEndProcess);
    });

    [Fact]
    public void ARestoreIsRecordedAsRunningUntilItHasReported() => UiThread.Run(async () =>
    {
        var key = new UninstallKey(UninstallScope.CurrentUser, "Tool");
        IReadOnlyList<RunningAction> whileRestoring = [];
        _runner.Replying(RegExe, _ =>
        {
            whileRestoring = _running.Current;
            _registry.With(UninstallScope.CurrentUser, "Tool", ("DisplayName", "Tool"));
            return null;
        });
        var file = Path.Combine(_temp.CreateDirectory("backups"), "tool.reg");
        File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", "", $"[{key.PhysicalPath}]", "\"DisplayName\"=\"Tool\""], System.Text.Encoding.Unicode);
        var page = Page();
        page.Actions.SelectedBackup = BackupRow.For(new RegistryBackupFile(file, key.PhysicalPath, "Tool", DateTimeOffset.UnixEpoch, IsConfined: true));

        await page.Actions.RestoreCommand.ExecuteAsync(null);

        Assert.Equal([RunningAction.BackupRestore], whileRestoring);
        Assert.True(_running.MayEndProcess);
        Assert.Equal("Restored. The entry is back in the list.", page.Actions.Report);
    });
}
