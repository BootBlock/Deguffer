using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
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

/// <summary>What the Installed apps page does with the backups it kept (§7.3), and where they are.</summary>
/// <param name="Folder">Where backups are kept, in display form.</param>
/// <param name="List">Lists the backups in <paramref name="Folder"/>: <see cref="RegistryBackups.List"/>, which reads files, so it is called off the UI thread.</param>
public sealed record BackupTools(
    BackupRestorer Restorer,
    BackupDeleter Deleter,
    string Folder,
    Func<CancellationToken, IReadOnlyList<RegistryBackupFile>> List);

/// <summary>
/// The Installed apps page's Backups tab (§7.3): the backups it kept, the ones selected, and
/// restoring one or deleting several. Every decision is Core's (<see cref="BackupRestorer"/>,
/// <see cref="BackupDeleter"/>).
///
/// <para>One action runs at a time across the whole page, and its report stands in the page's one
/// info bar, so each action here runs through <see cref="InstalledAppsActions.ActAsync"/>.</para>
/// </summary>
public sealed partial class InstalledAppsBackups : ObservableObject
{
    private readonly InstalledAppsActions _page;
    private readonly BackupTools _tools;
    private readonly Func<IInstalledAppsConfirmation> _confirmation;
    private readonly bool _isElevated;
    private IReadOnlyList<RegistryBackupFile> _selected = [];
    private CancellationTokenSource? _listing;

    /// <param name="page">The page's actions, whose <see cref="InstalledAppsActions.IsActing"/> holds these back while another action runs.</param>
    internal InstalledAppsBackups(InstalledAppsActions page, BackupTools tools, Func<IInstalledAppsConfirmation> confirmation, bool isElevated)
    {
        _page = page;
        _tools = tools;
        _confirmation = confirmation;
        _isElevated = isElevated;
        page.PropertyChanged += OnPageChanged;
    }

    public ObservableCollection<BackupRow> Rows { get; } = [];

    /// <summary>Where backups are kept, in display form.</summary>
    public string Folder => _tools.Folder;

    /// <summary>Why the selection cannot be restored, or what restoring it does.</summary>
    [ObservableProperty]
    public partial string RestoreNote { get; private set; } = string.Empty;

    public void Select(IReadOnlyList<BackupRow> selected)
    {
        _selected = [.. selected.Select(row => row.File)];
        ShowRestoreNote();
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Read the backup folder again, off the UI thread. A listing already running is abandoned for this one.</summary>
    public async Task ShowAsync()
    {
        _listing?.Cancel();
        using var listing = new CancellationTokenSource();
        _listing = listing;

        try
        {
            var rows = await Task.Run(() => _tools.List(listing.Token).Select(BackupRow.For).ToList(), listing.Token);

            // A newer listing may have started after this one's last check, and its rows are the
            // ones the list must show.
            if (!listing.IsCancellationRequested)
            {
                LiveList.Show(Rows, rows, row => row.File.Path);
            }
        }
        catch (OperationCanceledException) when (listing.IsCancellationRequested)
        {
            // Replaced by a newer listing, which shows its own rows.
        }
        finally
        {
            if (ReferenceEquals(_listing, listing))
            {
                _listing = null;
            }
        }
    }

    private void ShowRestoreNote() => RestoreNote = _tools.Restorer.MayRestore(_selected, _isElevated).Reason;

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledAppsActions.IsActing))
        {
            RestoreCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAct() => _selected.Count > 0 && !_page.IsActing;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestoreAsync()
    {
        var selected = _selected;
        var verdict = _tools.Restorer.MayRestore(selected, _isElevated);

        if (!verdict.IsAllowed || selected is not [var backup])
        {
            _page.Show(verdict.Reason, InfoBarSeverity.Informational);
            return;
        }

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForRestore(backup), CancellationToken.None))
        {
            _page.Show("Nothing was restored.", InfoBarSeverity.Informational);
            return;
        }

        await _page.ActAsync(RunningAction.BackupRestore, async () =>
        {
            var outcome = await Task.Run(() => _tools.Restorer.RestoreAsync(backup, _isElevated, CancellationToken.None));

            return new ActionReport(outcome.Message, outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error, Failed: !outcome.Succeeded);
        });

        // The same backup now meets an entry that is there, so its note says so.
        ShowRestoreNote();
        _page.EntriesHaveChanged();
    }

    /// <summary>
    /// Delete the selected backups for good, after the user confirms. Deguffer cannot restore an
    /// entry from a deleted file, so the question names every file and says so; the folder is listed
    /// again after.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteAsync()
    {
        var selection = _tools.Deleter.Choose(_selected);

        // Core gives a note whenever nothing may be deleted, an empty selection included.
        if (selection is { Deletable.Count: 0, Note: { } refusal })
        {
            _page.Show(refusal, InfoBarSeverity.Informational);
            return;
        }

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForBackupDeletion(selection.Deletable), CancellationToken.None))
        {
            _page.Show("Nothing was deleted.", InfoBarSeverity.Informational);
            return;
        }

        await _page.ActAsync(RunningAction.BackupDeletion, async () =>
        {
            var report = await Task.Run(() => _tools.Deleter.DeleteAsync(selection.Deletable, CancellationToken.None));

            IReadOnlyList<string> details = selection.Note is { } left ? [.. report.Details, left] : report.Details;

            return ActionReport.Of(report.Summary, details, report.IsComplete, report.Verification);
        });

        await ShowAsync();
    }
}
