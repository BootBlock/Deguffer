using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Which of the Storage page's controls are drawn, as <see cref="StorageControls"/> decides from
/// whether a scan has produced a row and whether the page is busy, told to the bindings each time
/// either changes.
/// </summary>
public sealed partial class StoragePageControls : ObservableObject
{
    private bool _rowsProduced;

    private bool _busy;

    /// <param name="findings">Every row of the page's scans, drawn or not.</param>
    internal StoragePageControls(ObservableCollection<FindingViewModel> findings)
    {
        // Followed rather than told by each place that adds a row, and never undone by a clear: see
        // StorageControls.ListControls.
        findings.CollectionChanged += (_, _) =>
        {
            if (!_rowsProduced && findings.Count > 0)
            {
                _rowsProduced = true;
                Changed();
            }
        };
    }

    /// <summary>Whether any scan on the page has produced a row, drawn or not.</summary>
    public bool HasProducedRows => _rowsProduced;

    /// <inheritdoc cref="StorageControls.ListControls"/>
    public bool ListControls => Shown.ListControls;

    /// <inheritdoc cref="StorageControls.ActionBar"/>
    public bool ActionBar => Shown.ActionBar;

    /// <inheritdoc cref="StorageControls.EmptyStateOffersScan"/>
    public bool EmptyStateOffersScan => Shown.EmptyStateOffersScan;

    private StorageControls Shown => StorageControls.For(_rowsProduced, _busy);

    /// <summary>Take up whether the page is busy, as it changes.</summary>
    internal void Follow(bool busy)
    {
        _busy = busy;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(HasProducedRows));
        OnPropertyChanged(nameof(ListControls));
        OnPropertyChanged(nameof(ActionBar));
        OnPropertyChanged(nameof(EmptyStateOffersScan));
    }
}
