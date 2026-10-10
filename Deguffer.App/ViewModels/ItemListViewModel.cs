using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Choosing;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One row's items, listed to be read and chosen one by one: under their headings, narrowed by a
/// search, and ticked a group at a time or an item at a time.
///
/// <para>The rules are Core's. <see cref="ItemGroups"/> orders the list, <see cref="ItemFilter"/>
/// decides what a search shows and counts the ticks it hides, and <see cref="ItemSelection"/> decides
/// what a checkbox standing for several items shows and what a click on it writes. This type keeps
/// the list on screen in step with the row. What a run takes is still the row's
/// <see cref="FindingViewModel.SelectedFinding"/>, narrowed in Core.</para>
/// </summary>
public sealed partial class ItemListViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Every item in order, worked out once. Nothing reorders while the list is open, and sorting a
    /// thousand items again on every keystroke would be work for an answer already known (G4).
    /// </summary>
    private readonly IReadOnlyList<ItemGroup<StepViewModel>> _ordered;

    /// <summary>A heading for each of <see cref="_ordered"/>, kept for as long as the list is open.</summary>
    private readonly IReadOnlyList<ItemGroupViewModel> _headings;

    private ItemFilter _filter = new(null);

    public ItemListViewModel(FindingViewModel row)
    {
        Row = row;
        _ordered = ItemGroups.Of(row.Steps, step => step.Step);
        IsGrouped = _ordered.Any(group => group.Name is not null);
        _headings = [.. _ordered.Select(group => new ItemGroupViewModel(row, group.Name))];

        Present();

        // A tick, a group click, a keep and a release all reach the row, and the row raises this once
        // for each of them, which is when every checkbox and figure here has to follow.
        row.SelectionChanged += OnRowSelectionChanged;
    }

    public FindingViewModel Row { get; }

    /// <summary>
    /// Whether the items fall under headings at all. A provider that names none is one list, and
    /// drawing it as a single group would put a heading with no name over it.
    /// </summary>
    public bool IsGrouped { get; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>
    /// The groups the search leaves on screen, each holding only the items it shows. Brought up to
    /// date in place, as every bound list is, so a search moves only the rows it hides or brings back.
    /// </summary>
    public ObservableCollection<ItemGroupViewModel> Shown { get; } = [];

    /// <summary>
    /// The same items without their headings: what a list with no headings shows, and what the
    /// checkbox over the whole list covers.
    /// </summary>
    public ObservableCollection<StepViewModel> ShownItems { get; } = [];

    public bool HasNoMatches => ShownItems.Count == 0;

    /// <summary>
    /// What the checkbox over every item on screen shows. It covers what the search shows and nothing
    /// else, so a click on it never ticks an item the reader cannot see.
    /// </summary>
    [ObservableProperty]
    public partial bool? AllShownState { get; private set; }

    [ObservableProperty]
    public partial bool CanToggleAllShown { get; private set; }

    /// <summary>How many of the row's items are ticked, of those that can be, and what they come to.</summary>
    public string SelectionLabel =>
        $"{Row.Steps.Count(step => step.IsSelected)} of {Row.Steps.Count(step => step.CanBeSelected)} selected, "
        + FreeSpace.Format(Row.SelectedSize);

    /// <summary>How many ticked items the search is hiding. See <see cref="ItemFilter.HiddenSelected"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HiddenSelectionLabel))]
    [NotifyPropertyChangedFor(nameof(HasHiddenSelection))]
    public partial int HiddenSelected { get; private set; }

    /// <summary>
    /// Said whenever the search hides a ticked item, because every one of them is still in the clean,
    /// and the shorter list on screen is exactly what would let a reader believe otherwise.
    /// </summary>
    public string HiddenSelectionLabel => HiddenSelected == 1
        ? "1 selected item is hidden by the search, and it is still in the clean."
        : $"{HiddenSelected} selected items are hidden by the search, and they are still in the clean.";

    public bool HasHiddenSelection => HiddenSelected > 0;

    public void Dispose() => Row.SelectionChanged -= OnRowSelectionChanged;

    [RelayCommand]
    private void ToggleAllShown() => Row.SetSelected(ShownItems, ItemSelection.ValueForClick(AllShownState));

    partial void OnQueryChanged(string value)
    {
        _filter = new ItemFilter(value);
        Present();
    }

    /// <summary>Bring the headings and the items on screen to what the search shows, in place.</summary>
    private void Present()
    {
        var each = _filter.ShowingEach(_ordered, step => step.Step);

        for (var at = 0; at < _headings.Count; at++)
        {
            _headings[at].Show(each[at] ?? []);
        }

        LiveList.Show(ShownItems, [.. each.SelectMany(items => items ?? [])], step => step);
        LiveList.Show(Shown, [.. _headings.Where((_, at) => each[at] is not null)], heading => heading);
        OnPropertyChanged(nameof(HasNoMatches));

        // Each heading works out its own checkbox as it is shown, so what is left is the list's own.
        RefreshFigures();
    }

    /// <summary>A tick, a keep or a release can move every heading's checkbox and every figure here.</summary>
    private void OnRowSelectionChanged(FindingViewModel row)
    {
        foreach (var group in Shown)
        {
            group.Refresh();
        }

        RefreshFigures();
    }

    /// <summary>Bring the checkbox over every shown item, the hidden count and the selected count up to date with the row.</summary>
    private void RefreshFigures()
    {
        AllShownState = ItemSelection.StateOf(ShownItems.Select(step => (step.IsSelected, step.CanBeSelected)));
        CanToggleAllShown = ShownItems.Any(step => step.CanBeSelected);
        HiddenSelected = _filter.HiddenSelected(Row.Steps.Select(step => (step.Step, step.IsSelected)));

        OnPropertyChanged(nameof(SelectionLabel));
    }
}
