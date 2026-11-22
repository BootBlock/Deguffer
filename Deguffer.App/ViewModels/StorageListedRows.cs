using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Deguffer.Core.Execution;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The rows the Storage list draws: every row of the findings that the filters let through, in the
/// same order, split between the list and the folded group below it by
/// <see cref="FindingViewModel.Place"/>.
///
/// <para>A row the filters hide leaves this list rather than collapsing where it stood, and a row
/// they show again joins it, so the list can show it going and coming. The commonest case is the end
/// of a clean: a row the clean emptied is re-planned as already clear, and leaves the list as the
/// rows below it close up.</para>
///
/// <para>Rows arrive as their providers finish and are cleared wholesale between runs, and following
/// the findings covers both without every mutation site having to bring the drawn rows along. A
/// clear starts a list about another scan, so the drawn rows are reset with it rather than removed
/// one by one: the list then knows that its scroll position points at rows that are gone
/// (<see cref="ListMotion.Keep"/>).</para>
/// </summary>
internal sealed class StorageListedRows
{
    private readonly ObservableCollection<FindingViewModel> _findings;

    /// <param name="findings">Every row of the scan, in the order the list shows them.</param>
    public StorageListedRows(ObservableCollection<FindingViewModel> findings)
    {
        _findings = findings;
        Empty = new StorageEmptyState(findings, this);

        findings.CollectionChanged += (_, changed) =>
        {
            if (changed.Action == NotifyCollectionChangedAction.Reset)
            {
                Replace();
            }
            else
            {
                Show();
            }
        };
    }

    public BoundList<FindingViewModel> Rows { get; } = [];

    /// <summary>The rows waiting for a source folder, folded into one group below <see cref="Rows"/>.</summary>
    public AwaitingFolderGroupViewModel AwaitingFolders { get; } = new();

    /// <summary>What the card says when it draws none of these, told after every update.</summary>
    public StorageEmptyState Empty { get; }

    /// <summary>
    /// Bring <see cref="Rows"/> up to the rows the filters let through, in place. Called for a change
    /// the findings raise nothing for: a filter, or a row re-planned where it stands.
    /// </summary>
    public void Show()
    {
        LiveList.Show(Rows, PlacedAt(PreviewPlace.Listed), row => row);
        AwaitingFolders.Show(PlacedAt(PreviewPlace.AwaitingSourceFolders));
        Empty.Changed();
    }

    private void Replace()
    {
        Rows.Replace(PlacedAt(PreviewPlace.Listed));
        AwaitingFolders.Replace(PlacedAt(PreviewPlace.AwaitingSourceFolders));
        Empty.Changed();
    }

    private FindingViewModel[] PlacedAt(PreviewPlace place) => [.. _findings.Where(row => row.Place == place)];
}
