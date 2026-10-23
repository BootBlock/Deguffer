using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Startup;
using Deguffer.Core.Viewing;
using Deguffer.Core.Working;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Startup page (§7.5): the reading, the one entry the user picked, its one change, and the
/// offers to reopen elevated and to open Windows' own Startup page. Every decision is Core's: what may
/// change is <see cref="StartupChangePolicy"/>'s, what a row says is <see cref="StartupMarks"/>', and
/// what a change did is <see cref="StartupSwitcher"/>'s.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly Func<CancellationToken, StartupView> _read;
    private readonly Func<StartupEntry, StartupChangeReport> _switch;
    private readonly bool _isElevated;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly Func<Task<bool>> _openSettings;
    private readonly RunningActions _running;
    private readonly BusyWork _work;
    private StartupView _view = StartupView.Empty;
    private CancellationTokenSource? _reads;

    /// <summary>An entry a link asked for, picked once a reading holds it.</summary>
    private StartupKey? _wanted;

    /// <param name="read">Reads every entry and what its program holds. Run off the UI thread.</param>
    /// <param name="change">Changes one entry, deciding again first. Run off the UI thread.</param>
    /// <param name="relaunch">Starts an elevated replacement, returning false where the user declined.</param>
    /// <param name="openSettings">Opens Windows' own Startup page, returning false where Windows would not.</param>
    /// <param name="running">What is changing the machine on every page, which the relaunch waits for.</param>
    public StartupViewModel(
        Func<CancellationToken, StartupView> read,
        Func<StartupEntry, StartupChangeReport> change,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch,
        Func<Task<bool>> openSettings,
        RunningActions running,
        BusyWork work)
    {
        _read = read;
        _switch = change;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _openSettings = openSettings;
        _running = running;
        _work = work;
        CanElevate = ElevationOffer.ShouldOffer(isElevated);

        // A change on this page, or a clean or a removal on another, ends with this process.
        _running.Changed += (_, _) => ElevateCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<StartupRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Headline { get; private set; } = "Reading what starts at sign-in…";

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ElevateCommand))]
    public partial bool CanElevate { get; private set; }

    /// <summary>The one entry the user picked. Nothing is ever picked for them (§7.5).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    public partial StartupRow? SelectedRow { get; set; }

    /// <summary>What the change button will do for the picked entry, or why it will not: a sentence, never a disabled button.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>The change button's word: the change the picked entry would get.</summary>
    [ObservableProperty]
    public partial string ChangeLabel { get; private set; } = "Disable";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    public partial bool IsActing { get; private set; }

    /// <summary>The last change's report, kept until dismissed: evidence that vanishes is not evidence (§5.6).</summary>
    [ObservableProperty]
    public partial string Report { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity ReportSeverity { get; private set; }

    [ObservableProperty]
    public partial bool HasReport { get; private set; }

    /// <summary>Raised when an elevated replacement has started, so the page can close this one.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    /// <summary>Pick <paramref name="key"/>'s entry, now if the list holds it and otherwise once a reading does.</summary>
    public void Reveal(StartupKey key)
    {
        _wanted = key;
        PickWanted();
    }

    partial void OnSelectedRowChanged(StartupRow? value) => Decide();

    /// <summary>Read every entry again. A read already running is abandoned for this one.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _reads?.Cancel();
        using var reads = new CancellationTokenSource();
        _reads = reads;
        IsReading = true;

        using var work = _work.Begin();

        try
        {
            var view = await Task.Run(() => _read(reads.Token), reads.Token);

            if (reads.IsCancellationRequested)
            {
                return;
            }

            _view = view;
            Headline = view.Headline;
            ShowRows();
        }
        catch (OperationCanceledException) when (reads.IsCancellationRequested)
        {
            // Replaced by a newer read, which reports for itself.
            return;
        }
        finally
        {
            if (ReferenceEquals(_reads, reads))
            {
                _reads = null;
                IsReading = false;
            }
        }
    }

    private bool CanChange() => SelectedRow is not null && !IsActing;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangeAsync()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var chosen = row.Entry;
        var verdict = StartupChangePolicy.MayChange(chosen, _isElevated);

        if (!verdict.IsAllowed)
        {
            Show(verdict.Reason, InfoBarSeverity.Informational);
            return;
        }

        IsActing = true;

        // Held until the report is on screen, so neither the window nor an Elevate button ends the
        // process before it has said what it did.
        using var running = _running.Begin(RunningAction.StartupChange);
        var work = _work.Begin();

        try
        {
            var report = await Task.Run(() => _switch(chosen));

            Show(
                string.Join(Environment.NewLine, [report.Summary, .. report.Details]),
                report.IsComplete ? InfoBarSeverity.Success
                    : report.Verification.Passed ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Error);

            if (!report.Verification.Passed)
            {
                work.Fail();
            }
        }
        finally
        {
            IsActing = false;
            work.Dispose();
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (!await _openSettings())
        {
            Show("Windows would not open its Startup page in Settings.", InfoBarSeverity.Warning);
        }
    }

    /// <summary>Elevating ends this process, so it waits for every action on every page.</summary>
    private bool CanElevateNow() => CanElevate && _running.MayEndProcess;

    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void Elevate()
    {
        if (_relaunch(ElevationRequest.Startup))
        {
            ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void DismissReport()
    {
        HasReport = false;
        Report = string.Empty;
    }

    private void ShowRows()
    {
        LiveList.Show(
            Rows,
            _view.Reading.Entries,
            row => row.Key,
            entry => entry.Key,
            entry => new StartupRow(entry, MarksFor(entry)),
            (row, entry) => row.Show(entry, MarksFor(entry)));

        // The picked row was written over in place, so what it may do is decided again from what it
        // holds now.
        Decide();
        PickWanted();
    }

    private StartupMarks MarksFor(StartupEntry entry) =>
        StartupMarks.For(entry, _view.Memory.For(entry.Key), _view.Memory.Figures, _isElevated);

    private void Decide()
    {
        if (SelectedRow is not { } row)
        {
            Note = string.Empty;
            return;
        }

        var verdict = StartupChangePolicy.MayChange(row.Entry, _isElevated);
        Note = verdict.Reason;
        ChangeLabel = verdict.Change switch
        {
            StartupChange.Enable => "Enable",
            StartupChange.Disable => "Disable",
            _ => row.Entry.State is StartupState.Disabled ? "Enable" : "Disable",
        };
    }

    private void PickWanted()
    {
        if (_wanted is { } key && Rows.FirstOrDefault(r => r.Key.Equals(key)) is { } row)
        {
            _wanted = null;
            SelectedRow = row;
        }
    }

    private void Show(string report, InfoBarSeverity severity)
    {
        Report = report;
        ReportSeverity = severity;
        HasReport = true;
    }
}
