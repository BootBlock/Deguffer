using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Viewing;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>A backup as the page lists it.</summary>
public sealed record BackupRow(RegistryBackupFile File, string Title, string Detail)
{
    public static BackupRow For(RegistryBackupFile file) => new(
        file,
        file.DisplayName ?? file.KeyPath,
        $"{file.Written.ToLocalTime():g} · {file.Path}");
}

/// <summary>
/// What the Installed apps page does to what the user selected (§7.3): remove stale entries,
/// uninstall one program, restore a backup. Every decision is Core's; this carries the selection
/// to it, asks the user, runs the action off the UI thread and keeps the report on screen until it
/// is dismissed.
/// </summary>
public sealed partial class InstalledAppsActions : ObservableObject
{
    private readonly EntryRemover _remover;
    private readonly BackupRestorer _restorer;
    private readonly ProgramUninstaller _uninstaller;
    private readonly RegistryBackups _backups;
    private readonly Func<IInstalledAppsConfirmation> _confirmation;
    private readonly PreferenceService _preferences;
    private readonly bool _isElevated;
    private IReadOnlyList<InstalledEntry> _stale = [];
    private InstalledEntry? _installed;
    private UninstallLaunch? _launch;
    private CancellationTokenSource? _watching;

    public InstalledAppsActions(
        EntryRemover remover,
        BackupRestorer restorer,
        ProgramUninstaller uninstaller,
        RegistryBackups backups,
        Func<IInstalledAppsConfirmation> confirmation,
        PreferenceService preferences,
        bool isElevated)
    {
        _remover = remover;
        _restorer = restorer;
        _uninstaller = uninstaller;
        _backups = backups;
        _confirmation = confirmation;
        _preferences = preferences;
        _isElevated = isElevated;
    }

    /// <summary>Raised after an action changed the entries, so the page reads them again.</summary>
    public event EventHandler? EntriesChanged;

    public ObservableCollection<BackupRow> Backups { get; } = [];

    /// <summary>The backup switch, remembered between runs.</summary>
    public bool BackUpFirst
    {
        get => _preferences.Current.BackUpInstalledAppEntries;
        set
        {
            if (!_preferences.Update(p => p with { BackUpInstalledAppEntries = value }))
            {
                Show("Deguffer could not save the backup setting, so it is unchanged.", InfoBarSeverity.Warning);
            }

            // Raised whether or not the save held, so the switch reads back the stored value.
            OnPropertyChanged();
        }
    }

    public string BackupFolder => _backups.Folder;

    /// <summary>Why some of the selected stale entries will be left, or empty.</summary>
    [ObservableProperty]
    public partial string RemovalNote { get; private set; } = string.Empty;

    /// <summary>Why the selected program cannot be uninstalled, or what will run.</summary>
    [ObservableProperty]
    public partial string UninstallNote { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RestoreNote { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(UninstallCommand), nameof(RestoreCommand))]
    public partial bool IsActing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopWatchingCommand))]
    public partial bool IsWatching { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial BackupRow? SelectedBackup { get; set; }

    /// <summary>The last action's report, kept until dismissed: evidence that vanishes is not evidence (§5.6).</summary>
    [ObservableProperty]
    public partial string Report { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity ReportSeverity { get; private set; }

    [ObservableProperty]
    public partial bool HasReport { get; private set; }

    public void SelectStale(IReadOnlyList<InstalledEntry> selected)
    {
        _stale = selected;
        RemovalNote = RemovalSelection.For(selected, _isElevated).Note ?? string.Empty;
        RemoveCommand.NotifyCanExecuteChanged();
    }

    public void SelectInstalled(InstalledEntry? selected)
    {
        _installed = selected;
        (var verdict, _launch, _) = selected is null ? (ActionVerdict.Refuse(string.Empty), null, null) : _uninstaller.Prepare(selected);
        UninstallNote = verdict.Reason;
        UninstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedBackupChanged(BackupRow? value) =>
        RestoreNote = value is null ? string.Empty : _restorer.MayRestore(value.File, _isElevated).Reason;

    /// <summary>Read the backup folder again.</summary>
    public void ShowBackups() =>
        LiveList.Show(Backups, [.. _backups.List().Select(BackupRow.For)], row => row.File.Path);

    private bool CanRemove() => _stale.Count > 0 && !IsActing;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        var selection = RemovalSelection.For(_stale, _isElevated);

        if (selection.Removable.Count == 0)
        {
            Show(selection.Note ?? "Nothing selected can be removed.", InfoBarSeverity.Informational);
            return;
        }

        var backUp = BackUpFirst;

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForRemoval(selection.Removable, backUp, BackupFolder), CancellationToken.None))
        {
            Show("Nothing was removed.", InfoBarSeverity.Informational);
            return;
        }

        IsActing = true;

        try
        {
            var report = await Task.Run(() => _remover.RemoveAsync(selection.Removable, backUp, _isElevated, CancellationToken.None));

            Show(
                string.Join(Environment.NewLine, [report.Summary, .. report.Details]),
                report.IsComplete ? InfoBarSeverity.Success : report.Verification.Passed ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        }
        finally
        {
            IsActing = false;
        }

        ShowBackups();
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUninstall() => _launch is not null && !IsActing;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (_installed is not { } chosen || _launch is not { } launch)
        {
            return;
        }

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForUninstall(chosen, launch), CancellationToken.None))
        {
            Show("Nothing was run.", InfoBarSeverity.Informational);
            return;
        }

        using var watching = new CancellationTokenSource();
        _watching = watching;
        IsActing = true;
        IsWatching = true;
        Show($"Waiting for the uninstaller of '{chosen.Name}' to finish…", InfoBarSeverity.Informational);

        try
        {
            var report = await Task.Run(() => _uninstaller.UninstallAsync(chosen, launch, watching.Token));

            Show(report.Summary, report.After is AfterUninstall.NotRun ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (watching.IsCancellationRequested)
        {
            Show($"Deguffer stopped watching the uninstaller of '{chosen.Name}'. It may still be running.", InfoBarSeverity.Informational);
        }
        finally
        {
            _watching = null;
            IsWatching = false;
            IsActing = false;
        }

        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(IsWatching))]
    private void StopWatching() => _watching?.Cancel();

    private bool CanRestore() => SelectedBackup is not null && !IsActing;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (SelectedBackup is not { } backup)
        {
            return;
        }

        var verdict = _restorer.MayRestore(backup.File, _isElevated);

        if (!verdict.IsAllowed)
        {
            Show(verdict.Reason, InfoBarSeverity.Informational);
            return;
        }

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForRestore(backup.File), CancellationToken.None))
        {
            Show("Nothing was restored.", InfoBarSeverity.Informational);
            return;
        }

        IsActing = true;

        try
        {
            var outcome = await Task.Run(() => _restorer.RestoreAsync(backup.File, _isElevated, CancellationToken.None));

            Show(outcome.Message, outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            IsActing = false;
        }

        // The same backup now meets an entry that is there, so its note says so.
        OnSelectedBackupChanged(SelectedBackup);

        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissReport()
    {
        HasReport = false;
        Report = string.Empty;
    }

    private void Show(string report, InfoBarSeverity severity)
    {
        Report = report;
        ReportSeverity = severity;
        HasReport = true;
    }
}
