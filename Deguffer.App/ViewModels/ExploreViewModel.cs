using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Drives the Explore page: pick a drive, scan it, and move around what was found.
///
/// <para>This orchestrates and formats. It holds no knowledge of how a volume is read — that is
/// <see cref="ExploreScanner"/>'s, and it chooses between §5.5's two routes on its own — and none
/// of how a rectangle is drawn, which belongs to the layout and the rasteriser in Core. What is
/// left here is which node is being looked at and what the screen says about it (G2).</para>
/// </summary>
public sealed partial class ExploreViewModel : ObservableObject
{
    private readonly ExploreScanner _scanner;
    private readonly IVolumeInventory _volumes;

    public ExploreViewModel(ExploreScanner scanner, IVolumeInventory volumes, ExploreActions actions)
    {
        _scanner = scanner;
        _volumes = volumes;

        Selection = new ExploreSelection(actions);

        // A removal and a scan of the same drive have no business overlapping, so each stands the
        // other down through the one busy flag. One direction each, rather than a flag both write:
        // the selection says when it is working, and this says when the page is free to act.
        Selection.Working += (_, working) => IsBusy = working;
        Selection.Reported += (_, sentence) => Status = sentence;
        Selection.Changed += (_, _) => Refresh();

        RefreshDrives();
    }

    /// <summary>What the user picked out by hand, and what §7.1 lets them do with it.</summary>
    public ExploreSelection Selection { get; }

    /// <summary>The volumes offered in the picker, as <c>C:\</c> roots.</summary>
    public ObservableCollection<string> Drives { get; } = [];

    /// <summary>
    /// What the current node holds, in the tree's own order: largest first once a scan has
    /// finished, and by name while one is still running. See <see cref="ExploreChildOrder"/>.
    /// </summary>
    public ObservableCollection<ExploreRow> Rows { get; } = [];

    /// <summary>The path from the scan's root down to the current node.</summary>
    public ObservableCollection<ExploreCrumb> Trail { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial string? SelectedDrive { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ElevateAndRescanCommand))]
    public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => Selection.CanAct = !value;

    /// <summary>
    /// How far through, 0 to 1, or null where the route cannot say.
    ///
    /// <para>The file table states its record count before the first read, so that route drives a
    /// real bar. A walk cannot know how many directories it has yet to open, so it gets an
    /// indeterminate one rather than a made-up denominator that would run to 90% and stop.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    [NotifyPropertyChangedFor(nameof(HasNoProgressFraction))]
    public partial double? Progress { get; set; }

    /// <summary>
    /// The same figure as a plain number, because a progress bar's value is not nullable and a
    /// binding that has to fall back is a binding that fails silently when it is wrong.
    /// </summary>
    public double ProgressValue => Progress ?? 0;

    /// <summary>Whether the bar has to be indeterminate. See <see cref="Progress"/>.</summary>
    public bool HasNoProgressFraction => Progress is null;

    [ObservableProperty]
    public partial string Status { get; set; } = "Choose a drive and scan it to see what is using the space.";

    /// <summary>The sentence §5.5 requires beside a walked scan, or null when the table answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouteNote))]
    public partial string? RouteNote { get; set; }

    public bool HasRouteNote => !string.IsNullOrEmpty(RouteNote);

    /// <summary>
    /// Whether to offer a relaunch as administrator, on the same terms the Storage page offers it:
    /// only where a scan actually fell back for want of rights.
    /// </summary>
    [ObservableProperty]
    public partial bool CanElevate { get; set; }

    /// <summary>
    /// The scan that is on screen. Replaced wholesale rather than mutated, so a snapshot arriving
    /// mid-scan cannot be half-applied while something is drawing from it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTree))]
    [NotifyPropertyChangedFor(nameof(HasNoTree))]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    [NotifyCanExecuteChangedFor(nameof(AscendCommand))]
    public partial ExploreTree? Tree { get; set; }

    /// <summary>
    /// Which picture the user asked for.
    ///
    /// <para>Here rather than only in the page, because the page is not the only thing that has to
    /// answer for it. While a scan is running the map draws the icicle whatever this says, and
    /// <see cref="ViewNote"/> is what keeps that from being a silent substitution.</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewNote))]
    [NotifyPropertyChangedFor(nameof(HasViewNote))]
    public partial ExploreView SelectedView { get; set; }

    /// <summary>
    /// How what is on screen differs from what the View box names, or null when it does not.
    ///
    /// <para>Only ever raised by a partial tree, and every view is affected by one. Its children are
    /// ordered by name rather than by size, so that a scan in progress stays still, and neither the
    /// treemap nor the sunburst can be drawn from that at all — squarification is defined only over
    /// a decreasing sequence, and a sunburst's residual wedge assumes the small children are the
    /// tail — so the map substitutes the icicle on top. A scan reading the file table publishes no
    /// partial tree, so it never says any of this.</para>
    ///
    /// <para>The list gets the sentence as much as the pictures do. It reorders itself alphabetically
    /// and back again, which is a substitution too, and one nobody is told about is the kind a user
    /// reads as a bug.</para>
    /// </summary>
    public string? ViewNote => Tree is { ChildOrder: not ExploreChildOrder.BySize }
        ? SelectedView switch
        {
            ExploreView.Treemap =>
                "Drawing the icicle, in name order, while the scan runs. A treemap reorders every "
                + "folder as it grows, so it follows when the scan finishes.",
            ExploreView.Sunburst =>
                "Drawing the icicle, in name order, while the scan runs. A sunburst turns every "
                + "wedge after one that grows, so it follows when the scan finishes.",
            _ =>
                "In name order while the scan runs, so nothing moves as a folder grows. Largest "
                + "first when the scan finishes.",
        }
        : null;

    public bool HasViewNote => ViewNote is not null;

    /// <summary>Which node the views are drawing. The scan's root until the user descends.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AscendCommand))]
    public partial int CurrentNode { get; set; }

    /// <summary>What is under the pointer, or the current node when nothing is.</summary>
    [ObservableProperty]
    public partial string Hovered { get; set; } = string.Empty;

    public bool HasTree => Tree is not null;

    /// <summary>
    /// Whether to show the empty state instead of a picture. A large blank card reads as a screen
    /// that has failed rather than one waiting to be told to start.
    /// </summary>
    public bool HasNoTree => Tree is null;

    /// <summary>
    /// Raised when the tree or the current node changed, so the map redraws. An event rather than
    /// the control watching several properties: a redraw is expensive and must happen once per
    /// change, not once per property that took part in it.
    /// </summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised once a replacement process is running and this one should stand down.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (SelectedDrive is not { } drive)
        {
            return;
        }

        IsBusy = true;
        Progress = null;
        RouteNote = null;
        CanElevate = false;
        Status = $"Scanning {drive}…";

        try
        {
            var scan = await _scanner.ScanAsync(drive, new Progress<ExploreProgress>(Report), ct);

            Selection.Reset(scan.Tree);
            Show(scan.Tree, scan.Tree.RootNode);

            RouteNote = scan.RouteNote;
            CanElevate = !ElevatedRelaunch.IsElevated && scan.Fallback == FallbackReason.NotElevated;

            Status = scan.Tree.HasUnknownSizes
                ? $"{FreeSpace.Format(scan.Tree.TotalBytes)} accounted for. Some of this drive could not "
                  + "be read, so the totals are lower bounds."
                : $"{FreeSpace.Format(scan.Tree.TotalBytes)} accounted for.";
        }
        catch (OperationCanceledException)
        {
            // The last snapshot goes with it. A partial tree covers only the levels walked so far,
            // its HasUnknownSizes is false because nothing refused anything, and it draws and
            // navigates exactly like a finished scan — so leaving it on screen states a total for
            // the drive that is wrong by however much was left.
            Tree = null;
            Selection.Reset(null);
            Rows.Clear();
            Trail.Clear();
            ViewChanged?.Invoke(this, EventArgs.Empty);

            Status = "Scan cancelled. Nothing was measured.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A volume that went away mid-scan, or one the account cannot open at all. Neither is
            // worth taking the window down for, and the page is read-only — there is nothing
            // half-done to report.
            Status = $"Could not scan {drive}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = null;
        }
    }

    /// <summary>
    /// §6.3: a process cannot grant itself rights it started without, so this starts a replacement
    /// and stands down — the same mechanism the Storage page uses, and for the same reason.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ElevateAndRescan()
    {
        if (!ElevatedRelaunch.TryRelaunch())
        {
            Status = "Deguffer is still running without administrator rights, so it scans by walking "
                + "directories. Everything else works exactly the same.";
            return;
        }

        ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Show what is inside <paramref name="node"/>. Ignored for anything with nothing in it.</summary>
    public void Descend(int node)
    {
        if (Tree is not { } tree || Selection.WasRemoved(node) || !tree.IsDirectory(node)
            || tree.ChildrenOf(node).Length == 0)
        {
            return;
        }

        Show(tree, node);
    }

    [RelayCommand(CanExecute = nameof(CanAscend))]
    private void Ascend()
    {
        if (Tree is { } tree && CurrentNode != tree.RootNode)
        {
            Show(tree, tree.ParentOf(CurrentNode));
        }
    }

    /// <summary>Jump straight to a node on the trail.</summary>
    public void GoTo(int node)
    {
        if (Tree is { } tree)
        {
            Show(tree, node);
        }
    }

    /// <summary>
    /// Say what the pointer is over. Called from the map on every move, so it formats and assigns
    /// and does nothing else — anything heavier here runs at the display's refresh rate.
    /// </summary>
    public void Hover(int? node, long? aggregateBytes)
    {
        Hovered = (Tree, node, aggregateBytes) switch
        {
            (_, _, { } bytes) => $"{FreeSpace.Format(bytes)} in items too small to draw separately",
            ({ } tree, { } value, _) => $"{tree.PathOf(value)} — {FreeSpace.Format(tree.SizeOf(value))}",
            _ => string.Empty,
        };
    }

    private void Report(ExploreProgress progress)
    {
        Progress = progress.Fraction;

        Status = progress.Total is null
            ? $"Scanning… {progress.Done:N0} items, {FreeSpace.Format(progress.BytesSeen)} so far"
            : $"Reading the file table… {progress.Fraction:P0}";

        // A partial tree, on the cadence the scanner chose. Drawing it is what stops a long scan
        // looking like a hung window — and it is only ever a snapshot, so the finished tree replaces
        // it rather than being merged into it.
        if (progress.Snapshot is { } snapshot)
        {
            Show(snapshot, snapshot.RootNode);
        }
    }

    /// <summary>
    /// Point every view at one node of one tree, in one place.
    ///
    /// <para>The rows, the trail and the map all describe the same thing, so they are rebuilt
    /// together. Updating them from separate handlers is how a breadcrumb comes to name a directory
    /// the list below it is no longer showing.</para>
    /// </summary>
    private void Show(ExploreTree tree, int node)
    {
        Tree = tree;
        CurrentNode = node;

        Selection.Show(tree);
        BuildRows(tree, node);
        BuildTrail(tree, node);

        Hovered = string.Empty;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuild the list and the picture from what is left, without rescanning.</summary>
    private void Refresh()
    {
        if (Tree is { } tree)
        {
            BuildRows(tree, CurrentNode);
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BuildRows(ExploreTree tree, int node)
    {
        Rows.Clear();

        var total = tree.SizeOf(node);

        foreach (var child in tree.ChildrenOf(node))
        {
            if (Selection.WasRemoved(child))
            {
                continue;
            }

            Rows.Add(new ExploreRow(
                child,
                tree.NameOf(child),
                Size(tree, child),
                total > 0 ? 100.0 * tree.SizeOf(child) / total : 0,
                tree.IsDirectory(child),
                tree.IsLink(child),
                tree.HasUnknownSizeBelow(child)));
        }
    }

    /// <summary>
    /// A row's size, marked where it is a lower bound.
    ///
    /// <para>A directory the walk was refused totals only what it could see, and the plain figure
    /// reads as a measurement. The page says so once in its status line, which is true and is not
    /// enough — it is the row for <c>System Volume Information</c> showing "0 B" that a reader
    /// acts on. <see cref="ExploreRow.Description"/> says the same in words for a screen reader,
    /// because a symbol read aloud is not a sentence.</para>
    /// </summary>
    private static string Size(ExploreTree tree, int node) =>
        tree.HasUnknownSizeBelow(node)
            ? "≥ " + FreeSpace.Format(tree.SizeOf(node))
            : FreeSpace.Format(tree.SizeOf(node));

    private void BuildTrail(ExploreTree tree, int node)
    {
        Trail.Clear();

        var steps = new List<ExploreCrumb>();

        for (var current = node; ; current = tree.ParentOf(current))
        {
            steps.Add(new ExploreCrumb(current, tree.NameOf(current)));

            if (current == tree.RootNode)
            {
                break;
            }
        }

        steps.Reverse();

        foreach (var step in steps)
        {
            Trail.Add(step);
        }
    }

    private void RefreshDrives()
    {
        _volumes.Invalidate();

        Drives.Clear();

        // Only volumes that can actually be read. An optical drive with no disc and a card reader
        // with no card are both mounted and both answer no, and offering them is offering a scan
        // that cannot start.
        foreach (var volume in _volumes.Volumes.Where(v => v.IsReady && v.Kind != DriveType.Network))
        {
            Drives.Add(volume.RootPath);
        }

        SelectedDrive = Drives.FirstOrDefault();
    }

    private bool CanScan() => !IsBusy && SelectedDrive is not null;

    private bool CanRun() => !IsBusy;

    private bool CanAscend() => Tree is { } tree && CurrentNode != tree.RootNode;
}
