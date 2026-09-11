using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Choosing;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One heading in a row's item list: its name, the items under it that the search shows, and the
/// checkbox that stands for those items.
///
/// <para>A list of its items rather than an object holding one, because that is the shape a grouped
/// <c>CollectionViewSource</c> reads without a property path. A path is resolved at run time and
/// fails silently, where a list is simply enumerated. Being a list, it cannot also derive from
/// <c>ObservableObject</c>, so its change notification is written out below.</para>
/// </summary>
public sealed partial class ItemGroupViewModel : List<StepViewModel>, INotifyPropertyChanged
{
    private readonly FindingViewModel _row;

    private bool? _state;
    private bool _canToggle;

    public ItemGroupViewModel(FindingViewModel row, ItemGroup<StepViewModel> group)
        : base(group.Items)
    {
        _row = row;

        // A provider that puts some items under headings and some under none still has to name the
        // second group, or its items would read as part of the heading above them.
        Title = group.Name ?? "Other items";

        var size = group.Items.Aggregate(ScanSize.Zero, (total, step) => total + step.Step.Reclaim);
        Summary = $"{(Count == 1 ? "1 item" : $"{Count} items")}, {FreeSpace.Format(size)}";

        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    /// <summary>What the heading's items come to, counting only those the search shows.</summary>
    public string Summary { get; }

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
