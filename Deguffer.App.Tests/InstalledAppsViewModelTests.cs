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

    public InstalledAppsViewModelTests() =>
        _preferences = new PreferenceService(new PreferenceStore(new FakeUserEnvironment(_temp.Path)));

    public void Dispose() => _temp.Dispose();

    private InstalledAppsViewModel Page(bool relaunchStarts = false)
    {
        var reader = new InstalledAppsReader(_registry, _installer, _paths, FixedSystemDirectories.Standard, new FakePackageDependencies());
        var backups = new RegistryBackups(_runner, Path.Combine(_temp.Path, "backups"), "reg.exe", TimeProvider.System);

        return new InstalledAppsViewModel(
            reader.Read,
            new InstalledAppsActions(
                new EntryRemover(_registry, reader, backups),
                new BackupRestorer(_registry, backups),
                new ProgramUninstaller(reader, new FakeUninstallLauncher(), @"C:\Windows\System32\msiexec.exe"),
                backups,
                () => _prompt,
                _preferences,
                isElevated: false),
            isElevated: false,
            request =>
            {
                _relaunches.Add(request);
                return relaunchStarts;
            });
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
        _runner.Replying(_ =>
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
}
