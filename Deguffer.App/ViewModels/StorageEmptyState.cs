using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the Storage card says in place of its rows when it draws none. On launch the list is a large
/// blank card, which reads as a screen that has failed rather than one waiting to be told to start.
/// Which of the three things it says is Core's decision (<see cref="EmptyPreviewReason"/>).
/// </summary>
public sealed partial class StorageEmptyState : ObservableObject
{
    private readonly ObservableCollection<FindingViewModel> _findings;

    private readonly StorageListedRows _listed;

    private readonly CleanViewModel _page;

    /// <summary>
    /// Why nothing is drawn, decided once per update rather than once per property the page reads. Left
    /// at its first value while rows are drawn, when nothing reads it.
    /// </summary>
    private EmptyPreview _reason;

    /// <param name="findings">Every row of the scan, drawn or not.</param>
    /// <param name="listed">What the card draws of them.</param>
    /// <param name="page">
    /// The page, whose <see cref="CleanViewModel.HasPreview"/> says whether the rows are the whole scan,
    /// and whose last clean's free-space move the card states once that clean has emptied the list.
    /// Its <see cref="CleanViewModel.RunResult"/> is set before this is built.
    /// </param>
    internal StorageEmptyState(ObservableCollection<FindingViewModel> findings, StorageListedRows listed, CleanViewModel page)
    {
        _findings = findings;
        _listed = listed;
        _page = page;
        page.PropertyChanged += OnPageChanged;
        page.RunResult.PropertyChanged += OnRunResultChanged;
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

    /// <summary>A tick for a result, and the search glyph over a list still to fill or to unfilter.</summary>
    public string Glyph => _reason is EmptyPreview.NothingLeftToClean or EmptyPreview.NothingToCleanSoFar ? "\uE73E" : "\uE721";

    public string Title => _reason switch
    {
        EmptyPreview.NothingLeftToClean => "Nothing left to clean",
        EmptyPreview.NothingToCleanSoFar => "Nothing to clean so far",
        EmptyPreview.HiddenByFilters => "Every row is hidden",
        _ => "Nothing scanned yet",
    };

    /// <summary>
    /// The sentence under the title. After a clean, it leads with how far the free space moved
    /// (<see cref="FreeSpaceMove.Sentence"/>), because that is what the clean was for, and the card
    /// with the figure may be scrolled out of sight.
    /// </summary>
    public string Message => _reason switch
    {
        EmptyPreview.NothingLeftToClean when _page.RunResult.Move is { } move =>
            $"{move.Sentence} Nothing the scan found has anything left to remove.",
        EmptyPreview.NothingLeftToClean => "Nothing the scan found has anything left to remove.",
        EmptyPreview.NothingToCleanSoFar =>
            "Nothing the scan has reached has anything to remove. The locations it has not reached may still hold something.",
        EmptyPreview.HiddenByFilters =>
            "The scan found only locations whose tools are not installed, and the filter above the list "
            + "hides them. Tick “Show items not installed” to list them.",
        _ => "A scan looks at the locations Deguffer recognises and reports what each one holds. It "
             + "reads only — nothing is removed until you choose to.",
    };

    /// <summary>
    /// The words of the button that lists the clear locations, empty where there are none to list.
    /// The button ticks the same box as the bar over the list (<see cref="AlreadyClearSwitch.ShowCommand"/>).
    /// </summary>
    public string Remedy => _reason is EmptyPreview.NothingLeftToClean or EmptyPreview.NothingToCleanSoFar ? "Show items that are already clear" : string.Empty;

    /// <summary>
    /// Decide again why nothing is drawn, and tell the page to read everything again. Called by
    /// <see cref="StorageListedRows"/> after every update of what is drawn, which includes a filter
    /// change that changes nothing in the findings themselves.
    /// </summary>
    internal void Changed()
    {
        _reason = IsShown
            ? EmptyPreviewReason.Of(_findings.Select(static row => row.Status), _page.HasPreview ? PreviewExtent.Whole : PreviewExtent.Partial)
            : EmptyPreview.NothingScanned;
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Remedy));
    }

    /// <summary>A scan that finishes turns "so far" into a verdict, and one that starts turns it back.</summary>
    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanViewModel.HasPreview))
        {
            Changed();
        }
    }

    /// <summary>The free space is read again after the run ends, and the sentence follows each reading.</summary>
    private void OnRunResultChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanRunResultViewModel.Move))
        {
            OnPropertyChanged(nameof(Message));
        }
    }
}
