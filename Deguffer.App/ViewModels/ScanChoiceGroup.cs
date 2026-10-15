using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Choosing;
using Deguffer.Core.Providers;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One category's heading in the list of what to scan, and the rows the search shows under it. The
/// heading's tick covers those rows: a search for "cache" and a tick on Games chooses the game caches
/// on screen, never a row the reader cannot see.
/// </summary>
public sealed partial class ScanChoiceGroup : ObservableCollection<ScanChoiceRow>
{
    private readonly ScanChoiceViewModel _owner;
    private bool? _state;

    public ScanChoiceGroup(ScanChoiceViewModel owner, ProviderCategory category)
    {
        _owner = owner;
        Category = category;
        Title = ProviderListing.Title(category);
    }

    public ProviderCategory Category { get; }

    public string Title { get; }

    public string ToggleName => $"Every location shown under {Title}";

    public bool? State
    {
        get => _state;
        private set
        {
            if (value != _state)
            {
                _state = value;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(State)));
            }
        }
    }

    internal void Show(IReadOnlyList<ScanChoiceRow> rows)
    {
        LiveList.Show(this, rows, row => row.Provider);
        Refresh();
    }

    /// <summary>Say the heading's tick has changed even where it has not. See <see cref="ScanChoiceRow.Republish"/>.</summary>
    internal void Republish() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(State)));

    internal void Refresh() =>
        State = ItemSelection.StateOf(this.Select(row => (row.IsChosen, CanBeSelected: true)));

    [RelayCommand]
    private void Toggle() => _owner.Choose(this.Select(row => row.Provider), ItemSelection.ValueForClick(State));
}
