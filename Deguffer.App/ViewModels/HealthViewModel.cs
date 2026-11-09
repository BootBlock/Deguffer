using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Health;
using Deguffer.Core.Viewing;
using Deguffer.Core.Working;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Health page (§7.6): the drives, the one the reader is looking at, and the offer to reopen
/// elevated. A read here changes nothing, so it is never a <see cref="RunningActions"/> action and
/// never holds the window open. Each reading's meaning is Core's.
/// </summary>
public sealed partial class HealthViewModel : ObservableObject
{
    private const string NothingListed = "Windows would not list this computer's drives.";

    private readonly Func<CancellationToken, Task<IReadOnlyList<DriveDescription>?>> _list;
    private readonly Func<IReadOnlyList<DriveDescription>, IProgress<DriveHealth>, IProgress<DriveVolumes>, CancellationToken, Task> _read;
    private readonly bool _isElevated;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly RunningActions _running;
    private readonly BusyWork _work;
    private CancellationTokenSource? _reads;
    private bool _hasRead;

    /// <param name="list">Lists the drives.</param>
    /// <param name="read">Reads each drive and what is stored on it, reporting each as it finishes.</param>
    /// <param name="relaunch">Starts an elevated replacement, returning false where the user declined.</param>
    /// <param name="running">What is changing the machine on every page, which the relaunch waits for.</param>
    public HealthViewModel(
        Func<CancellationToken, Task<IReadOnlyList<DriveDescription>?>> list,
        Func<IReadOnlyList<DriveDescription>, IProgress<DriveHealth>, IProgress<DriveVolumes>, CancellationToken, Task> read,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch,
        RunningActions running,
        BusyWork work)
    {
        _list = list;
        _read = read;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _running = running;
        _work = work;

        _running.Changed += (_, _) => ElevateCommand.NotifyCanExecuteChanged();
    }

    public event EventHandler? ReplacedByElevatedInstance;

    public ObservableCollection<HealthDriveRow> Rows { get; } = [];

    [ObservableProperty]
    public partial HealthDriveRow? SelectedDrive { get; set; }

    [ObservableProperty]
    public partial string Headline { get; private set; } = "Reading your drives…";

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    /// <summary>Whether administrator rights would show more of a drive. Drives the banner and the button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ElevateCommand))]
    public partial bool CanElevate { get; private set; }

    /// <summary>The first read, on the first arrival. A return to the kept page reads nothing (§7.6).</summary>
    public void EnsureRead()
    {
        if (_hasRead)
        {
            return;
        }

        _hasRead = true;
        _ = RefreshAsync();
    }

    /// <summary>
    /// Read every drive again. A read still running is abandoned for this one, and concurrent runs
    /// are allowed so that a stalled drive never greys out the button.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task RefreshAsync()
    {
        _reads?.Cancel();
        using var reads = new CancellationTokenSource();
        _reads = reads;
        IsReading = true;

        using var work = _work.Begin();

        try
        {
            var drives = await _list(reads.Token);

            if (reads.IsCancellationRequested)
            {
                return;
            }

            if (drives is null)
            {
                Headline = NothingListed;
                return;
            }

            ShowDrives(drives);

            // A report posted just before this read was abandoned must not write over a newer one.
            var health = new Progress<DriveHealth>(report =>
            {
                if (!reads.IsCancellationRequested)
                {
                    Show(report);
                }
            });

            var volumes = new Progress<DriveVolumes>(line =>
            {
                if (!reads.IsCancellationRequested)
                {
                    RowOf(line.Key)?.Show(line);
                }
            });

            await _read(drives, health, volumes, reads.Token);
        }
        catch (OperationCanceledException) when (reads.IsCancellationRequested)
        {
            // Replaced by a newer read, which reports for itself.
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

    private bool CanElevateNow() => CanElevate && _running.MayEndProcess;

    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void Elevate()
    {
        if (_relaunch(ElevationRequest.Health))
        {
            ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The one place a drive leaves the list, because only a finished listing knows which have gone.
    /// A failed or abandoned read never reaches here, so it removes nothing.
    /// </summary>
    private void ShowDrives(IReadOnlyList<DriveDescription> drives)
    {
        var place = SelectedDrive is { } selected ? Rows.IndexOf(selected) : 0;

        LiveList.Show(
            Rows,
            drives,
            row => row.Key,
            drive => drive.Key,
            drive => new HealthDriveRow(drive),
            (row, drive) => row.Show(drive));

        SelectedDrive = DriveSelection.After(Rows, SelectedDrive, place);
        Summarise();
    }

    private void Show(DriveHealth report)
    {
        if (RowOf(report.Key) is { } row)
        {
            row.Show(report);
            Summarise();
        }
    }

    private HealthDriveRow? RowOf(DriveKey key) => Rows.FirstOrDefault(row => row.Key == key);

    private void Summarise()
    {
        var read = Rows.Select(row => row.Report).OfType<DriveHealth>().ToList();

        CanElevate = ElevationOffer.ShouldOffer(_isElevated, read);
        Headline = DriveOverview.Headline(Rows.Count, read);
    }
}
