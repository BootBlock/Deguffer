using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.InstalledApps;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Installed apps page (§7.3): the reading, the two lists as the reader narrows them, and the
/// offer to reopen elevated. What may be done to a selection is <see cref="InstalledAppsActions"/>.
/// </summary>
public sealed partial class InstalledAppsViewModel : ObservableObject
{
    private readonly Func<CancellationToken, InstalledAppsReading> _read;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly bool _isElevated;
    private InstalledAppsReading _reading = new([], []);
    private CancellationTokenSource? _reads;

    /// <param name="read">Reads every entry. Run off the UI thread.</param>
    /// <param name="relaunch">Starts an elevated replacement, returning false where the user declined.</param>
    public InstalledAppsViewModel(
        Func<CancellationToken, InstalledAppsReading> read,
        InstalledAppsActions actions,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch)
    {
        _read = read;
        _relaunch = relaunch;
        _isElevated = isElevated;
        Actions = actions;
        Actions.EntriesChanged += (_, _) => _ = RefreshAsync();
        CanElevate = ElevationOffer.ShouldOffer(isElevated);
    }

    public InstalledAppsActions Actions { get; }

    public ObservableCollection<InstalledAppRow> StaleRows { get; } = [];

    public ObservableCollection<InstalledAppRow> InstalledRows { get; } = [];

    [ObservableProperty]
    public partial string Headline { get; private set; } = "Reading the installed apps list…";

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ElevateCommand))]
    public partial bool CanElevate { get; private set; }

    /// <summary>Whether this process holds administrator rights, as the page's words need it.</summary>
    public bool IsElevated => _isElevated;

    /// <summary>Text the lists are narrowed to.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>Whether entries Windows does not list are shown too.</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>Raised when an elevated replacement has started, so the page can close this one.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    partial void OnFilterChanged(string value) => ShowLists();

    partial void OnShowHiddenChanged(bool value) => ShowLists();

    /// <summary>Read every entry again. A read already running is abandoned for this one.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _reads?.Cancel();
        using var reads = new CancellationTokenSource();
        _reads = reads;
        IsReading = true;

        try
        {
            var reading = await Task.Run(() => _read(reads.Token), reads.Token);

            _reading = reading;
            Headline = InstalledAppsLists.Headline(reading);
            ShowLists();
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

    [RelayCommand(CanExecute = nameof(CanElevate))]
    private void Elevate()
    {
        if (_relaunch(ElevationRequest.InstalledApps))
        {
            ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowLists()
    {
        var lists = InstalledAppsLists.From(_reading.Entries, ShowHidden, Filter);

        Show(StaleRows, lists.Stale);
        Show(InstalledRows, lists.Installed);
    }

    private static void Show(ObservableCollection<InstalledAppRow> rows, IReadOnlyList<InstalledEntry> entries) =>
        LiveList.Show(rows, entries, row => row.Key, entry => entry.Key, entry => new InstalledAppRow(entry), (row, entry) => row.Show(entry));
}
