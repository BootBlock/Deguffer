using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Retrim;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The offer of Windows' retrim after a clean, and the run it starts.
///
/// <para>Which volumes are offered is <see cref="RetrimOffer"/>'s decision, what the offer and its
/// results say is <see cref="RetrimReport"/>'s, and which volumes are retrimmed when asked is
/// <see cref="RetrimRun"/>'s. This type wires them to the Storage page, and restarts Deguffer as
/// administrator where Windows will not retrim for it as it is.</para>
/// </summary>
public sealed partial class RetrimViewModel : ObservableObject
{
    private readonly IVolumeInventory _volumes;
    private readonly IStorageQueries _storage;
    private readonly IVolumeRetrim _retrim;
    private readonly bool _isElevated;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly RunningActions _running;

    private IReadOnlyList<RetrimCandidate> _offered = [];

    public RetrimViewModel(
        IVolumeInventory volumes,
        IStorageQueries storage,
        IVolumeRetrim retrim,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch,
        RunningActions running)
    {
        _volumes = volumes;
        _storage = storage;
        _retrim = retrim;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _running = running;

        // A restart as administrator ends this process, so it waits for every page's work, as the
        // page's own Elevate button does.
        _running.Changed += (_, _) => RetrimCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised once a restart as administrator is on its way, so the page can close this instance.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    /// <summary>Whether the offer is on screen: a clean freed enough on a volume a retrim is offered on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool IsOffered { get; set; }

    /// <summary>The offer, a sentence for each volume. See <see cref="RetrimReport.Offer"/>.</summary>
    [ObservableProperty]
    public partial string OfferText { get; set; } = string.Empty;

    /// <summary>What the button that accepts the offer says.</summary>
    [ObservableProperty]
    public partial string ActionLabel { get; set; } = string.Empty;

    /// <summary>Whether Windows is retrimming now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    [NotifyCanExecuteChangedFor(nameof(RetrimCommand))]
    public partial bool IsRetrimming { get; set; }

    /// <summary>What Windows is doing now, while <see cref="IsRetrimming"/>.</summary>
    [ObservableProperty]
    public partial string Underway { get; set; } = string.Empty;

    /// <summary>What became of each retrim asked for, a sentence each, in the order they ended.</summary>
    public ObservableCollection<string> Results { get; } = [];

    /// <summary>Whether there is anything to show: the offer, a retrim under way, or what one answered.</summary>
    public bool IsShown => IsOffered || IsRetrimming || Results.Count > 0;

    /// <summary>
    /// Offer a retrim on each fixed volume whose free space grew enough since
    /// <paramref name="before"/>, read by <see cref="FreeSpaceReading.Take"/> before the clean. Asks
    /// the storage behind those volumes off the calling thread, once each for this offer.
    /// </summary>
    public async Task OfferAsync(IReadOnlyList<LocalVolume> before, CancellationToken ct)
    {
        var after = FreeSpaceReading.Take(_volumes);
        var media = new VolumeMediaCache(_storage);

        try
        {
            _offered = await Task.Run(() => RetrimOffer.For(before, after, media.Of), ct);
        }
        catch (OperationCanceledException)
        {
            // Asked as a clean ends, under the clean's token, and an offer cancelled with it is no offer.
            // The clean has reported for itself by then.
            _offered = [];
        }

        IsOffered = _offered.Count > 0;
        OfferText = IsOffered ? RetrimReport.Offer(_offered, _isElevated) : string.Empty;
        ActionLabel = IsOffered ? RetrimReport.Action(_offered, _isElevated) : string.Empty;
    }

    /// <summary>
    /// Retrim the volumes a restart as administrator was asked to, with no offer first: the user
    /// accepted it in the instance this one replaced.
    /// </summary>
    public Task RunRequestedAsync(RetrimRequest request, CancellationToken ct) => RunAsync(request.Volumes, ct);

    /// <summary>
    /// Take the offer and its results down, as a new clean begins and they describe one that has gone.
    /// A retrim under way is left to finish and report, since what Windows answers is still true.
    /// </summary>
    public void Clear()
    {
        if (IsRetrimming)
        {
            return;
        }

        _offered = [];
        IsOffered = false;
        Results.Clear();
        OnPropertyChanged(nameof(IsShown));
    }

    [RelayCommand(CanExecute = nameof(CanRetrim))]
    private async Task RetrimAsync(CancellationToken ct)
    {
        IReadOnlyList<string> roots = [.. _offered.Select(candidate => candidate.Volume.RootPath)];

        if (_isElevated)
        {
            await RunAsync(roots, ct);
            return;
        }

        if (_relaunch(new RetrimRequest(roots)))
        {
            ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
            return;
        }

        // The offer stays, so the user can accept Windows' prompt the second time.
        Show(RetrimReport.NotRestarted);
    }

    private bool CanRetrim() => !IsRetrimming && (_isElevated || _running.MayEndProcess);

    private async Task RunAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        IsOffered = false;
        Results.Clear();
        IsRetrimming = true;

        // A retrim is the machine being changed, so the window waits for it before it closes, and a
        // countdown after a clean waits for it before it ends the session.
        using var running = _running.Begin(RunningAction.Retrim);

        try
        {
            var volumes = await _volumes.RefreshAsync(ct);
            var media = new VolumeMediaCache(_storage);
            var remaining = new Queue<string>(roots);
            Underway = remaining.Count > 0 ? RetrimReport.Underway(remaining.Peek()) : string.Empty;

            var landed = new Landed(result =>
            {
                remaining.TryDequeue(out _);
                Underway = remaining.TryPeek(out var next) ? RetrimReport.Underway(next) : string.Empty;
                Show(result.Sentence);
            });

            await RetrimRun.ExecuteAsync(roots, volumes, media.Of, _retrim, landed, ct);
        }
        catch (OperationCanceledException)
        {
            // The run stops only between drives, so every retrim Windows began has reported by now.
            Show(RetrimReport.Cancelled);
        }
        finally
        {
            IsRetrimming = false;
            Underway = string.Empty;
        }
    }

    private void Show(string sentence)
    {
        Results.Add(sentence);
        OnPropertyChanged(nameof(IsShown));
    }

    /// <summary>
    /// Reports where it is called, rather than posting as <see cref="Progress{T}"/> does. The run
    /// reports on the thread that awaits it, which is the page's, so a result is on screen before the
    /// run is seen to end.
    /// </summary>
    private sealed class Landed(Action<RetrimResult> report) : IProgress<RetrimResult>
    {
        public void Report(RetrimResult value) => report(value);
    }
}
