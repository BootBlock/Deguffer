using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Text;
using Deguffer.Core.Working;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>What an action on the Installed apps page reported, for the page's info bar.</summary>
/// <param name="Failed">Whether the action failed, which marks the work under way as failed.</param>
internal readonly record struct ActionReport(string Text, InfoBarSeverity Severity, bool Failed)
{
    /// <summary>A report of an action with a §5.6 check: a summary, a line for each thing left or unchecked, and a severity from both.</summary>
    public static ActionReport Of(string summary, IReadOnlyList<string> details, bool isComplete, VerificationResult verification) => new(
        string.Join(Environment.NewLine, [summary, .. details]),
        isComplete ? InfoBarSeverity.Success : verification.Passed ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
        Failed: !verification.Passed);
}

/// <summary>
/// What the Installed apps page does to what the user selected (§7.3): remove stale entries and
/// uninstall programs one at a time, with the backups the removals keep in <see cref="Backups"/>.
/// Every decision is Core's; this carries the selection
/// to it, asks the user, runs the action off the UI thread and keeps the report on screen until it
/// is dismissed.
/// </summary>
public sealed partial class InstalledAppsActions : ObservableObject
{
    private readonly EntryRemover _remover;
    private readonly ProgramUninstaller _uninstaller;
    private readonly UninstallQueue _queue;
    private readonly Func<IInstalledAppsConfirmation> _confirmation;
    private readonly PreferenceService _preferences;
    private readonly bool _isElevated;
    private readonly RunningActions _running;
    private readonly BusyWork _work;
    private IReadOnlyList<InstalledEntry> _stale = [];
    private IReadOnlyList<InstalledEntry> _installed = [];
    private bool _anyUninstallable;
    private CancellationTokenSource? _watching;

    /// <param name="running">What is changing the machine on every page, where each action is recorded while it runs.</param>
    public InstalledAppsActions(
        EntryRemover remover,
        BackupTools backups,
        ProgramUninstaller uninstaller,
        Func<IInstalledAppsConfirmation> confirmation,
        PreferenceService preferences,
        bool isElevated,
        RunningActions running,
        BusyWork work)
    {
        _remover = remover;
        _uninstaller = uninstaller;
        _queue = new UninstallQueue(uninstaller, confirmation);
        _confirmation = confirmation;
        _preferences = preferences;
        _isElevated = isElevated;
        _running = running;
        _work = work;
        Backups = new InstalledAppsBackups(this, backups, confirmation, isElevated);
    }

    /// <summary>Raised after an action changed the entries, so the page reads them again.</summary>
    public event EventHandler? EntriesChanged;

    /// <summary>The Backups tab: the backups the removals keep, restored or deleted from there.</summary>
    public InstalledAppsBackups Backups { get; }

    /// <summary>The backup switch, remembered between runs.</summary>
    public bool BackUpFirst
    {
        get => _preferences.Current.BackUpInstalledAppEntries;
        set
        {
            if (_preferences.Update(p => p with { BackUpInstalledAppEntries = value }) is SaveOutcome.Refused)
            {
                Show("Deguffer could not save the backup setting, so it is unchanged.", InfoBarSeverity.Warning);
            }

            // Raised whether or not the save held, so the switch reads back the stored value.
            OnPropertyChanged();
        }
    }

    /// <summary>Why some of the selected stale entries will be left, or empty.</summary>
    [ObservableProperty]
    public partial string RemovalNote { get; private set; } = string.Empty;

    /// <summary>What will run for the selected programs, and why any will be left.</summary>
    [ObservableProperty]
    public partial string UninstallNote { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(UninstallCommand))]
    public partial bool IsActing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopWatchingCommand))]
    public partial bool IsWatching { get; private set; }

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

    public void SelectInstalled(IReadOnlyList<InstalledEntry> selected)
    {
        _installed = selected;
        var selection = UninstallSelection.For(selected, _uninstaller.Judge);
        _anyUninstallable = selection.Runnable.Count > 0;
        UninstallNote = selection.Note;
        UninstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What <paramref name="entry"/>'s row shows beside its name.</summary>
    public EntryMarks MarksFor(InstalledEntry entry) => EntryMarks.For(entry, _uninstaller.Judge(entry).Verdict, _isElevated);

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

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForRemoval(selection.Removable, backUp, Backups.Folder), CancellationToken.None))
        {
            Show("Nothing was removed.", InfoBarSeverity.Informational);
            return;
        }

        await ActAsync(RunningAction.EntryRemoval, async () =>
        {
            var report = await Task.Run(() => _remover.RemoveAsync(selection.Removable, backUp, _isElevated, CancellationToken.None));

            return ActionReport.Of(report.Summary, report.Details, report.IsComplete, report.Verification);
        });

        // The entries are read again while the backups are listed, rather than after.
        var listing = Backups.ShowAsync();
        EntriesHaveChanged();
        await listing;
    }

    /// <summary>
    /// Run one action that changes the machine: one at a time across the page, held in
    /// <see cref="RunningActions"/> until its report is on screen, so neither the window nor an
    /// Elevate button ends the process before it has said what it did, and counted as work under way
    /// until then. What follows the report, such as listing the backups again, changes nothing, so
    /// it does not hold the window open.
    /// </summary>
    internal async Task ActAsync(RunningAction action, Func<Task<ActionReport>> run)
    {
        IsActing = true;

        using (_running.Begin(action))
        {
            var work = _work.Begin();

            try
            {
                var report = await run();
                Show(report.Text, report.Severity);

                if (report.Failed)
                {
                    work.Fail();
                }
            }
            finally
            {
                IsActing = false;
                work.Dispose();
            }
        }
    }

    /// <summary>Tell the page to read the entries again, after an action changed them.</summary>
    internal void EntriesHaveChanged() => EntriesChanged?.Invoke(this, EventArgs.Empty);

    private bool CanUninstall() => _anyUninstallable && !IsActing;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var chosen = _installed;
        var selection = await Task.Run(() => UninstallSelection.For(chosen, _uninstaller.Prepare));

        if (selection.Runnable.Count == 0)
        {
            Show(selection.Note, InfoBarSeverity.Informational);
            return;
        }

        if (!await _confirmation().AskAsync(InstalledAppsPrompt.ForUninstall(selection.Runnable), CancellationToken.None))
        {
            Show("Nothing was run.", InfoBarSeverity.Informational);
            return;
        }

        using var watching = new CancellationTokenSource();
        _watching = watching;
        IsActing = true;
        IsWatching = true;

        using var running = _running.Begin(RunningAction.Uninstall);
        var work = _work.Begin();

        try
        {
            var report = await _queue.RunAsync(
                selection.Runnable,
                new Progress<UninstallStep>(step =>
                {
                    ShowWaiting(step);
                    work.Report(step.Fraction);
                }),
                watching.Token);

            Show(
                report.Summary,
                report.Abandoned is not null ? InfoBarSeverity.Informational
                    : report.IsComplete ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        }
        finally
        {
            _watching = null;
            IsWatching = false;
            IsActing = false;
            work.Dispose();
        }

        EntriesHaveChanged();
    }

    private void ShowWaiting(UninstallStep step) => Show(
        Counted.Form(
            step.Count,
            $"Waiting for the uninstaller of '{step.Item.Entry.Name}' to finish…",
            $"Waiting for the uninstaller of '{step.Item.Entry.Name}' to finish ({step.Number} of {step.Count})…"),
        InfoBarSeverity.Informational);

    /// <summary>Stop watching the uninstaller running, and start no more of the queue.</summary>
    [RelayCommand(CanExecute = nameof(IsWatching))]
    private void StopWatching() => _watching?.Cancel();

    [RelayCommand]
    private void DismissReport()
    {
        HasReport = false;
        Report = string.Empty;
    }

    internal void Show(string report, InfoBarSeverity severity)
    {
        Report = report;
        ReportSeverity = severity;
        HasReport = true;
    }
}
