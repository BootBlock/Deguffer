using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Explore page's offer to relaunch as administrator and scan where it is pointed.
///
/// <para>Separate from <see cref="ExploreTargeting"/> because it holds state of its own, whether a
/// scan has finished and what it found, which the target does not; it only follows the target. The
/// decisions are Core's: whether elevating would help (<see cref="ElevationOffer"/>) and when the page
/// may act on it (<see cref="ExploreGate.MayElevate"/>).</para>
/// </summary>
public sealed partial class ExploreElevation : ObservableObject
{
    private readonly ExploreTargeting _targeting;
    private readonly IVolumeInventory _volumes;

    /// <summary>Whether this process holds administrator rights, which decides the offer.</summary>
    private readonly bool _isElevated;

    /// <summary>
    /// Starts an elevated replacement pointed where this page is, and says whether one started. A
    /// real relaunch raises the UAC prompt, which is why it is handed in.
    /// </summary>
    private readonly Func<ExploreRequest, bool> _relaunch;

    /// <summary>What is changing the machine on every page, which the relaunch waits for.</summary>
    private readonly RunningActions _running;

    /// <summary>The page's status line, which says so when the relaunch does not start.</summary>
    private readonly StatusLineViewModel _status;

    /// <summary>
    /// Whether a finished scan covers what the page is pointed at now. Not whether a tree is drawn,
    /// which a snapshot fills in while a scan is still running — a half-drawn map is not a scan the
    /// offer may be read from. Written only by <see cref="Offer"/>.
    /// </summary>
    private bool _hasScanned;

    /// <param name="isElevated">Whether this process holds administrator rights.</param>
    /// <param name="relaunch">See <see cref="_relaunch"/>.</param>
    /// <param name="running">See <see cref="_running"/>.</param>
    /// <param name="status">See <see cref="_status"/>.</param>
    internal ExploreElevation(
        ExploreTargeting targeting,
        IVolumeInventory volumes,
        bool isElevated,
        Func<ExploreRequest, bool> relaunch,
        RunningActions running,
        StatusLineViewModel status)
    {
        _targeting = targeting;
        _volumes = volumes;
        _isElevated = isElevated;
        _relaunch = relaunch;
        _running = running;
        _status = status;

        _targeting.Retargeted += (_, from) => Follow(from);

        // A clean or a removal on another page ends with this process as surely as one here.
        _running.Changed += (_, _) => ElevateAndRescanCommand.NotifyCanExecuteChanged();

        // Offered before anything has been scanned, so an elevated scan does not have to be reached
        // through the walked one it replaces.
        Offer(null, default);
    }

    /// <summary>Raised once a replacement process is running and this one should stand down.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    /// <summary>
    /// Whether to offer a relaunch as administrator, on the same terms the Storage page offers it:
    /// before anything is scanned, and afterwards only where the scan actually fell back for want
    /// of rights.
    ///
    /// <para>This says whether elevating would help, not whether the page is free to act on it —
    /// that is the command's own <c>CanExecute</c>.</para>
    /// </summary>
    [ObservableProperty]
    public partial bool CanElevate { get; private set; }

    /// <summary>What that button says. See <see cref="ElevationOffer.Label"/>.</summary>
    public string ElevateLabel => ElevationOffer.Label(_hasScanned);

    /// <summary>
    /// When the page may act, from the work under way and the picture on the map. The page hands it
    /// over as either changes. See <see cref="ExploreGate"/>.
    /// </summary>
    public ExploreGate Gate
    {
        get;
        set
        {
            field = value;
            ElevateAndRescanCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Say whether elevating would help, from whatever is known at the point of asking.
    ///
    /// <para>One place, because the two halves of the answer are one fact: what the button offers
    /// and what it says both turn on whether a scan has finished, and setting them apart is how a
    /// button comes to offer a rescan of a scan that never ran.</para>
    /// </summary>
    /// <param name="found">
    /// Why the finished scan walked, or null where no scan has finished — which is the state before
    /// the first one, and the state a cancelled or failed one returns the page to.
    /// </param>
    /// <param name="hidden">What Windows states about the space on the scanned volume that no folder holds.</param>
    public void Offer(FallbackReason? found, HiddenSpace hidden)
    {
        _hasScanned = found is not null;

        CanElevate = found is { } fallback
            ? ElevationOffer.ShouldOffer(_isElevated, fallback, hidden)
            : ElevationOffer.ShouldOffer(_isElevated);

        OnPropertyChanged(nameof(ElevateLabel));
    }

    /// <summary>
    /// The page was pointed somewhere new, from <paramref name="from"/>.
    ///
    /// <para>The offer describes what pressing the button would scan, not what is drawn on screen, so
    /// a move that changes what the next scan covers puts it back to the state before anything was
    /// measured. Leaving it alone is how the button comes to be hidden for a volume nothing has
    /// looked at, and how it comes to offer a rescan of a drive that was never scanned. A move that
    /// scans the same place leaves it, so a reading of the volumes that hands the chosen drive back
    /// does not take away an offer the last scan made.</para>
    ///
    /// <para>Whether the button may be pressed is asked again either way, because a reading can
    /// change whether the chosen volume is refused without moving the target at all.</para>
    /// </summary>
    private void Follow(ExploreTarget from)
    {
        if (!from.ScansTheSameAs(_targeting.Target))
        {
            Offer(null, default);
        }

        ElevateAndRescanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// §6.3: a process cannot grant itself rights it started without, so this starts a replacement
    /// and stands down — the same mechanism the Storage page uses, and for the same reason.
    ///
    /// <para>The replacement is told where this page was pointed, so it opens on Explore and scans
    /// it. The user pressed this while pointed somewhere, and landing them on another page with the
    /// drive box back at its default, and a picked folder thrown away, would not be that.</para>
    ///
    /// <para>What travels is what the page is pointed at now rather than what the last scan
    /// covered. The picker is on screen and the Scan button beside it would use exactly these two
    /// values, so a second, hidden idea of the target is one the page could then contradict — and it
    /// is why this asks <see cref="ExploreGate.MayElevate"/>, which is the Scan button's answer too,
    /// rather than only whether the page is busy. A relaunch with nothing to point at leaves the
    /// replacement waiting on a page the user did not ask to be on.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void ElevateAndRescan()
    {
        var target = _targeting.Target;

        if (!_relaunch(new ExploreRequest(target.Drive, target.Folder)))
        {
            _status.Say(StatusLine.Said(
                "Deguffer is still running without administrator rights, so it scans by walking "
                + "directories. Everything else works exactly the same."));
            return;
        }

        ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
    }

    private bool CanElevateNow() => Gate.MayElevate(_targeting.Target, _volumes, _running);
}
