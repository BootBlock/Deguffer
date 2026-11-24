using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Text;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The rows of the Storage list waiting for a source folder, folded into one group below the rest
/// (<see cref="PreviewPlace.AwaitingSourceFolders"/>), and the line that stands for them while the
/// group is closed.
/// </summary>
public sealed partial class AwaitingFolderGroupViewModel : ObservableObject
{
    /// <summary>The rows the group draws when it is open, in the order the list shows them.</summary>
    public BoundList<FindingViewModel> Rows { get; } = [];

    /// <summary>Whether the group is drawn at all. A group of nothing would be a heading for nothing.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>
    /// What the closed group says it holds. The count is the point: it is how a closed group tells
    /// the reader that something is folded away, and how much. It names no page, because the button
    /// that adds the folder is beside it.
    /// </summary>
    public string Summary =>
        $"{Counted.Of(Rows.Count, "location", "locations")} with nothing to search until you add a source folder.";

    /// <summary>
    /// Bring <see cref="Rows"/> up to <paramref name="arriving"/> in place, so an open group keeps
    /// its rows' containers and the reader's place among them.
    /// </summary>
    public void Show(IReadOnlyList<FindingViewModel> arriving)
    {
        var had = Rows.Count;
        LiveList.Show(Rows, arriving, row => row);
        Recount(had);
    }

    /// <summary>Start again from <paramref name="arriving"/>, for a list that is about another scan.</summary>
    public void Replace(IEnumerable<FindingViewModel> arriving)
    {
        var had = Rows.Count;
        Rows.Replace(arriving);
        Recount(had);
    }

    /// <summary>
    /// Both properties are read off the count alone, and this runs for every row a scan adds to the
    /// list, so an unchanged count tells the page nothing.
    /// </summary>
    private void Recount(int had)
    {
        if (Rows.Count == had)
        {
            return;
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Summary));
    }
}
