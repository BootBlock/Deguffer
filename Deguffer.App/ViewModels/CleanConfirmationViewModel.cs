using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The clean's one confirmation as the user answers it. Every figure and every word it shows, and
/// whether the answers let the clean start, come from the <see cref="CleanConfirmation"/>; this type
/// holds the ticks and the typing, and says when what depends on them has changed.
/// </summary>
public sealed partial class CleanConfirmationViewModel : ObservableObject
{
    public CleanConfirmationViewModel(CleanConfirmation confirmation)
    {
        Confirmation = confirmation;

        // One row model per item, shared by the groups and the flat list, so a tick given in a group is
        // the tick the replies read. Keyed by reference: two items are equal records where two selected
        // rows carry the same plan, and each still needs its own tick.
        var rows = new Dictionary<CleanConfirmationItem, ConfirmationRowViewModel>(ReferenceEqualityComparer.Instance);

        foreach (var item in confirmation.Items)
        {
            rows[item] = new ConfirmationRowViewModel(item);
        }

        Rows = [.. confirmation.Items.Select(item => rows[item])];
        Groups =
        [
            .. confirmation.Groups.Select(group =>
                new ConfirmationGroupViewModel(group, [.. group.Items.Select(item => rows[item])])),
        ];

        foreach (var row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    public CleanConfirmation Confirmation { get; }

    public IReadOnlyList<ConfirmationGroupViewModel> Groups { get; }

    /// <summary>Every row, in the order selected.</summary>
    public IReadOnlyList<ConfirmationRowViewModel> Rows { get; }

    public IReadOnlyList<CleanConfirmationReply> Replies => [.. Rows.Select(row => row.Reply)];

    public string Title => Confirmation.TitleOf(Replies);

    public string ConfirmLabel => Confirmation.ConfirmLabelOf(Replies);

    public string TotalLabel => Confirmation.TotalOf(Replies);

    /// <summary>Whether the button that starts the clean may be pressed.</summary>
    public bool CanConfirm => Confirmation.IsSatisfiedBy(Replies);

    public string Then => Confirmation.Then ?? string.Empty;

    public bool HasThen => Confirmation.Then is not null;

    public string ClosingLine => CleanConfirmation.ClosingLine;

    public bool HasClosingLine => Confirmation.RemovesFiles;

    /// <summary>
    /// Every answer feeds the title, the button, the total and whether the button is enabled, so a
    /// change to any row says all four have changed, in one event each.
    /// </summary>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConfirmationRowViewModel.IsIncluded)
            or nameof(ConfirmationRowViewModel.IsAcknowledged)
            or nameof(ConfirmationRowViewModel.TypedPhrase))
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ConfirmLabel));
            OnPropertyChanged(nameof(TotalLabel));
            OnPropertyChanged(nameof(CanConfirm));
        }
    }
}
