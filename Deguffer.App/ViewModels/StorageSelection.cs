using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Choosing;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What is ticked across the Storage page's rows, as one selection: each change to it, wherever it
/// came from, is remembered for the next scan and totalled for the Clean button.
///
/// <para>Apart from <see cref="CleanViewModel"/>, which runs the scans and the clean, because the
/// selection changes for reasons of its own: a click on a row or an item, or an entry of the selection
/// menu that ticks every row at once. Which rows an entry ticks is Core's
/// (<see cref="BulkSelectionRule"/>).</para>
/// </summary>
public sealed partial class StorageSelection : ObservableObject
{
    private readonly IReadOnlyList<FindingViewModel> _rows;
    private readonly SelectionService _selections;
    private readonly bool _isElevated;
    private readonly Func<bool> _mayTickMany;

    /// <summary>
    /// The rows a menu entry has changed so far, while it runs, or null between entries. See
    /// <see cref="OnRowChanged"/>.
    /// </summary>
    private List<FindingViewModel>? _changedByMenu;

    /// <param name="rows">Every row of the scan, which this follows as they are added and removed.</param>
    /// <param name="selections">Where each change is remembered for the next scan.</param>
    /// <param name="isElevated">Whether this process holds administrator rights, which some steps need.</param>
    /// <param name="mayTickMany">
    /// Whether the menu may change the rows now: the page is idle, and the rows rather than one row's
    /// items are on screen. A menu entry changes rows the reader can see, never ones a scan is still
    /// adding or a clean is about to replace.
    /// </param>
    public StorageSelection(
        IReadOnlyList<FindingViewModel> rows,
        SelectionService selections,
        bool isElevated,
        Func<bool> mayTickMany)
    {
        _rows = rows;
        _selections = selections;
        _isElevated = isElevated;
        _mayTickMany = mayTickMany;
    }

    /// <summary>
    /// Raised once for each change the user made: one click, or one menu entry however many rows it
    /// changed. Not raised when a scan or the keep list changes the rows, which their owner reports.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>What the Clean button says: the action and what the ticked steps would give back.</summary>
    [ObservableProperty]
    public partial string CleanLabel { get; private set; } = CleanOffer.Label(ScanSize.Zero);

    /// <summary>Whether any row is ticked, which a clean needs.</summary>
    public bool IsAnyTicked => _rows.Any(row => row.IsSelected);

    /// <summary>
    /// Sums the selected <em>steps</em> rather than the selected rows. With per-item selection a
    /// ticked row no longer implies its whole plan will run, so totalling the finding would promise
    /// back space that the unticked steps within it are not going to release.
    /// </summary>
    public ScanSize Total =>
        ReclaimPool.Total(_rows.SelectMany(row => row.SelectedSteps).Select(s => s.Step));

    /// <summary>
    /// The ceiling <see cref="Total"/> can reach: every step the user is free to tick. This is what the
    /// info bar means by "can be cleaned", and it is summed the same way the selected total is so that
    /// the two figures beside each other count the same bytes. Both count space two rows share once:
    /// see <see cref="ReclaimPool"/>.
    /// </summary>
    public ScanSize SelectableTotal =>
        ReclaimPool.Total(_rows.SelectMany(row => row.SelectableSteps).Select(s => s.Step));

    /// <summary>
    /// Follow <paramref name="row"/>'s ticks. One event for both directions: the row's own checkbox and
    /// any step within it. Subscribing to PropertyChanged(IsSelected) alone would miss a step being
    /// unticked while the row stays ticked, which is the ordinary case for per-item selection.
    /// </summary>
    public void Follow(FindingViewModel row) => row.SelectionChanged += OnRowChanged;

    /// <summary>Stop following a row a scan is about to throw away.</summary>
    public void Forget(FindingViewModel row) => row.SelectionChanged -= OnRowChanged;

    /// <summary>
    /// State the figure again, for a change the rows raise no selection change for: a row added
    /// already ticked, a row re-planned, or a keep that took a ticked item out of its row.
    /// </summary>
    public void Retotal()
    {
        CleanLabel = CleanOffer.Label(Total);
        RefreshMenu();
    }

    /// <summary>Ask the menu entries again whether they would change anything, and whether they may.</summary>
    public void RefreshMenu()
    {
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectRecommendedCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll() => Select(BulkSelection.All);

    private bool CanSelectAll() => CanSelect(BulkSelection.All);

    [RelayCommand(CanExecute = nameof(CanSelectRecommended))]
    private void SelectRecommended() => Select(BulkSelection.Recommended);

    private bool CanSelectRecommended() => CanSelect(BulkSelection.Recommended);

    [RelayCommand(CanExecute = nameof(CanSelectNone))]
    private void SelectNone() => Select(BulkSelection.None);

    private bool CanSelectNone() => CanSelect(BulkSelection.None);

    /// <summary>
    /// Offered only where it would change a tick, so an entry that would do nothing is shown disabled
    /// rather than accepting a click that does nothing.
    /// </summary>
    private bool CanSelect(BulkSelection selection) =>
        _mayTickMany() && _rows.Any(row => WouldChange(row, TickFor(selection, row)));

    /// <summary>
    /// Apply <paramref name="selection"/> to every row through the row's own checkbox, as one thing the
    /// user did: every changed row is remembered in one write, and the figure, the menu and
    /// <see cref="Changed"/> follow once.
    /// </summary>
    private void Select(BulkSelection selection)
    {
        // Asked again rather than trusted to the disabled entry, because a scan can start between the
        // menu opening and the click landing.
        if (!_mayTickMany())
        {
            return;
        }

        _changedByMenu = [];

        foreach (var row in _rows)
        {
            var tick = TickFor(selection, row);

            if (WouldChange(row, tick))
            {
                row.SetSelected(row.Steps, tick == RowTick.Tick);
            }
        }

        var changed = _changedByMenu;
        _changedByMenu = null;

        Remember(changed);
    }

    private RowTick TickFor(BulkSelection selection, FindingViewModel row) =>
        BulkSelectionRule.For(selection, row.Finding, row.Place == PreviewPlace.Listed, row.IsSelected, _isElevated);

    /// <summary>
    /// Whether applying <paramref name="tick"/> changes a step of <paramref name="row"/>. Asked before
    /// the row is touched, because a row that changes nothing still raises its event when it is set.
    /// </summary>
    private static bool WouldChange(FindingViewModel row, RowTick tick) => tick switch
    {
        RowTick.Tick => row.SelectableSteps.Any(step => !step.IsSelected),
        RowTick.Clear => row.Steps.Any(step => step.IsSelected),
        _ => false,
    };

    /// <summary>
    /// One row's ticks changed. A menu entry collects its rows and records them once it has set them
    /// all, so a menu entry over a hundred rows is one copy of the remembered selection and one total,
    /// not a hundred of each.
    /// </summary>
    private void OnRowChanged(FindingViewModel row)
    {
        if (_changedByMenu is { } changed)
        {
            changed.Add(row);
            return;
        }

        Remember((FindingViewModel[])[row]);
    }

    /// <summary>
    /// Written on every change rather than at some tidier moment, because there is no reliable later
    /// one. A scan can be cancelled, a clean rebuilds the rows it changed from what was remembered, and
    /// the process can be replaced outright by the elevated relaunch: all three would lose a choice that
    /// was only being held until the end. The write runs on a worker (<see cref="SelectionService"/>),
    /// and the close and the relaunch wait for it.
    /// </summary>
    private void Remember(IReadOnlyList<FindingViewModel> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        _selections.Remember(rows.Select(row => KeyValuePair.Create(row.Finding.Provider.Id, row.ToRemembered())));
        Retotal();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
