using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Deguffer.Core.Viewing;

/// <summary>
/// A bound list that can also be rebuilt in one change, for the one case where a rebuild is what the
/// reader should see: a list about something else entirely.
///
/// <para>Every update of the same list goes through <see cref="LiveList"/>, which keeps the rows that
/// stay and tells the list as little as it can (a live list is updated in place, never rebuilt). A
/// list about another folder, another scan or another part of the machine has no rows worth keeping,
/// and its scroll position and selection are about content that is no longer there, so it is reset
/// anyway. <see cref="Collection{T}.Clear"/> followed by an insertion per row gets there through one
/// reset and then one change per row, each of which crosses into the UI framework and is answered by
/// the list's item collection and its panel. A step into a folder of twenty thousand entries was
/// twenty thousand of them in one turn of the UI thread, to arrive where one reset would have.</para>
/// </summary>
public sealed class BoundList<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));

    // The name ObservableCollection itself raises for its indexer.
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    private static readonly NotifyCollectionChangedEventArgs Reset = new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Replace everything in the list with <paramref name="arriving"/>, and tell the list once.
    /// A list that was empty and stays empty is told nothing.
    /// </summary>
    public void Replace(IEnumerable<T> arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        CheckReentrancy();

        var had = Items.Count;

        try
        {
            Items.Clear();

            foreach (var item in arriving)
            {
                Items.Add(item);
            }
        }
        finally
        {
            // Told even when making a row threw part of the way through, because the items have
            // changed under the list either way, and a list that is not told what it holds is one
            // whose next change names a row it never had.
            if (had > 0 || Items.Count > 0)
            {
                OnPropertyChanged(CountChanged);
                OnPropertyChanged(IndexerChanged);
                OnCollectionChanged(Reset);
            }
        }
    }
}
