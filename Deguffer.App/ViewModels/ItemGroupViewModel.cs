using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Choosing;
using Deguffer.Core.Scanning;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One heading in a row's item list: its name, the items under it that the search shows, and the
/// checkbox that stands for those items.
///
/// <para>A list of its items rather than an object holding one, because that is the shape a grouped
/// <c>CollectionViewSource</c> reads without a property path. A path is resolved at run time and
/// fails silently, where a list is simply enumerated. Being a list, it cannot also derive from
/// <c>ObservableObject</c>, so its change notification is written out below.</para>
///
/// <para>One for each heading for as long as the list is open, its items brought to what a search shows
/// in place, so a search moves only the rows it hides or brings back (<see cref="LiveList"/>).</para>
/// </summary>
public sealed partial class ItemGroupViewModel : ObservableCollection<StepViewModel>
{
    private readonly FindingViewModel _row;

    private bool? _state;
    private bool _canToggle;
    private string _summary = string.Empty;

    public ItemGroupViewModel(FindingViewModel row, string? name)
    {
        _row = row;
        Name = name;

        // A provider that puts some items under headings and some under none still has to name the
        // second group, or its items would read as part of the heading above them.
        Title = name ?? "Other items";
    }

    /// <summary>The heading as <see cref="ItemGroups"/> names it, null for the items under none.</summary>
    public string? Name { get; }

    public string Title { get; }

    /// <summary>What the heading's items come to, counting only those the search shows.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>What a screen reader calls the heading's checkbox, which has no visible label of its own.</summary>
    public string ToggleName => $"Every item shown under {Title}";

    /// <summary>See <see cref="ItemSelection.StateOf"/>.</summary>
    public bool? State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public bool CanToggle
    {
        get => _canToggle;
        private set => Set(ref _canToggle, value);
    }

    /// <summary>Show <paramref name="items"/> under this heading, and what they come to.</summary>
    public void Show(IReadOnlyList<StepViewModel> items)
    {
        LiveList.Show(this, items, step => step);

        var size = this.Aggregate(ScanSize.Zero, (total, step) => total + step.Step.Reclaim);
        Summary = $"{(Count == 1 ? "1 item" : $"{Count} items")}, {FreeSpace.Format(size)}";

        Refresh();
    }

    public void Refresh()
    {
        State = ItemSelection.StateOf(this.Select(step => (step.IsSelected, step.CanBeSelected)));
        CanToggle = this.Any(step => step.CanBeSelected);
    }

    [RelayCommand]
    private void Toggle() => _row.SetSelected(this, ItemSelection.ValueForClick(State));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(new PropertyChangedEventArgs(property));
    }
}
