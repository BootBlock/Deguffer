using System.Collections.Specialized;

namespace Deguffer.App.Tests;

/// <summary>
/// What a bound collection told its list, in order. A test of a list kept up to date in place
/// asserts the notifications rather than only the contents, because a Reset and a run of moves leave
/// the same items behind and only one of them keeps the reader's place.
/// </summary>
internal static class CollectionChanges
{
    /// <summary>Record every change <paramref name="list"/> raises from now on.</summary>
    public static List<NotifyCollectionChangedAction> Watch(INotifyCollectionChanged list)
    {
        var changes = new List<NotifyCollectionChangedAction>();

        list.CollectionChanged += (_, e) => changes.Add(e.Action);

        return changes;
    }
}
