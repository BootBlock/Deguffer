using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Storage card says in place of its rows when it draws none. On launch the list is a large
/// blank card, which reads as a screen that has failed rather than one waiting to be told to start.
/// </summary>
public sealed partial class StorageEmptyState : ObservableObject
{
    private readonly ObservableCollection<FindingViewModel> _findings;

    private readonly StorageListedRows _listed;

    /// <param name="findings">Every row of the scan, drawn or not.</param>
    /// <param name="listed">What the card draws of them.</param>
    internal StorageEmptyState(ObservableCollection<FindingViewModel> findings, StorageListedRows listed)
    {
        _findings = findings;
        _listed = listed;
    }

    /// <summary>
    /// Whether to show this instead of the list.
    ///
    /// <para>Asked of what is drawn rather than of what was found, because a filter can empty the
    /// list under a scan that found plenty. The commonest case is the one that follows success:
    /// clean everything, let the run re-plan, and every row is then either clear or absent — both
    /// hidden by default. Counting rows instead put a blank card at the end of a run that
    /// worked. The folded group counts as drawn: closed, it still says what it holds.</para>
    /// </summary>
    public bool IsShown => _listed.Rows.Count == 0 && !_listed.AwaitingFolders.HasRows;

    /// <summary>
    /// Whether that empty list is the filters' doing rather than an empty machine. The two need
    /// different words: one stands over Scan, and the other says why a scan that finished is
    /// showing nothing, and that both filters are in the bar over the list.
    /// </summary>
    private bool IsHiddenByFilters => _findings.Count > 0 && IsShown;

    public string Title => IsHiddenByFilters ? "Every row is hidden" : "Nothing scanned yet";

    public string Message => IsHiddenByFilters
        ? "The scan found locations, and the filters above the list are hiding all of them. Tick "
          + "“Show items not installed” or “Show items that are already clear” to list them."
        : "A scan looks at the locations Deguffer recognises and reports what each one holds. It "
          + "reads only — nothing is removed until you choose to.";

    /// <summary>
    /// Tell the page to read all three again. Called by <see cref="StorageListedRows"/> after every
    /// update of what is drawn, which includes a filter change that changes nothing in the findings
    /// themselves.
    /// </summary>
    internal void Changed()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Message));
    }
}
