using System.Collections.Frozen;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The items the user has asked Deguffer never to offer, across every provider.
///
/// <para><b>It persists where a tick may not, and the reason is the direction each fails in.</b>
/// <see cref="SelectionMemory"/> refuses to restore a tick on Tier 3, because a tick restored from an
/// earlier session is a pre-selection whoever made it, and on user data nothing stands behind a
/// pre-selection. An entry here that is lost or misread does the opposite: it offers something the user
/// wanted kept, and the preview and §7's confirmations still stand between that and a deletion. That
/// holds only while nothing here can be read as a tick, which is why this is a type and a file of its
/// own rather than a field beside the selection. One file holding both is one bug away from reading
/// a protection as a selection.</para>
///
/// <para>Immutable. The shell replaces one list with the next, so a change whose write fails can leave
/// the list in effect exactly as it was.</para>
/// </summary>
public sealed class KeepList
{
    private readonly FrozenDictionary<string, FrozenSet<string>> _keysByProvider;

    public KeepList(IEnumerable<KeptItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // Keys are directory names more often than not, so two that differ only in case name one
        // item. The later entry wins, which lets an item kept again under a newer name show that name.
        var byProvider = new Dictionary<string, Dictionary<string, KeptItem>>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!byProvider.TryGetValue(item.ProviderId, out var keys))
            {
                byProvider[item.ProviderId] = keys = new(StringComparer.OrdinalIgnoreCase);
            }

            keys[item.Item.Key] = item;
        }

        Items = [.. byProvider.Values.SelectMany(keys => keys.Values)];

        _keysByProvider = byProvider.ToFrozenDictionary(
            provider => provider.Key,
            provider => provider.Value.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }

    /// <summary>Nothing kept, which is a first run and every failure to read the stored list.</summary>
    public static KeepList Empty { get; } = new([]);

    /// <summary>Every kept item, for whoever lists them or writes them to disk.</summary>
    public IReadOnlyList<KeptItem> Items { get; }

    /// <summary>
    /// The keys kept for one provider, in the form <see cref="Execution.CleanupPlan.Keeping"/> takes.
    /// A set rather than the items, because matching a step is the only question a plan asks of it.
    /// </summary>
    public IReadOnlySet<string> KeysFor(string providerId) =>
        _keysByProvider.TryGetValue(providerId, out var keys) ? keys : FrozenSet<string>.Empty;

    /// <summary>This list with <paramref name="item"/> kept as well.</summary>
    public KeepList With(KeptItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new([.. Items, item]);
    }

    /// <summary>This list with one provider's item released, and every other entry untouched.</summary>
    public KeepList Without(string providerId, string key) => new(Items.Where(item =>
        !(string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
          && string.Equals(item.Item.Key, key, StringComparison.OrdinalIgnoreCase))));
}
