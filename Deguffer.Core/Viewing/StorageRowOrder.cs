using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Viewing;

/// <summary>
/// What the Storage page orders a row by: its name, the space it offers, its tier, and the place of
/// its provider, which breaks every tie.
/// </summary>
/// <param name="Size">The row's own figure, so a kept item does not place a row by space it will never offer.</param>
/// <param name="Place">The place of the row's provider, as <see cref="Execution.CleanupPlanner.PlaceOf"/> gives it.</param>
public readonly record struct OrderedRow(string Name, long Size, SafetyTier Tier, int Place);

/// <summary>
/// Where a row stands in the Storage page's list under the order the user chose (§7), both while the
/// list is still filling and when the order is changed under a finished one.
///
/// <para>Rows arrive as their providers finish (§5.5), so each is inserted where it belongs rather
/// than the list being sorted once the last one lands, which would reshuffle it under the reader.</para>
///
/// <para><b>A tie goes by the provider's place, not by when the row arrived</b>, in every order.
/// Providers are planned several at once and finish in whatever order their work takes, so rows that
/// compare equal, most of all the many that found nothing, would otherwise stand in a different
/// order after every scan.</para>
/// </summary>
public static class StorageRowOrder
{
    private static readonly IComparer<OrderedRow> BySize = Comparer<OrderedRow>.Create(
        static (a, b) => Then(b.Size.CompareTo(a.Size), a, b));

    // Invariant rather than ordinal, because a reader expects "npm" beside "NuGet" and not after
    // every capital; invariant rather than current, so the list is the same on every machine.
    private static readonly IComparer<OrderedRow> ByName = Comparer<OrderedRow>.Create(
        static (a, b) => Then(StringComparer.InvariantCultureIgnoreCase.Compare(a.Name, b.Name), a, b));

    // Largest first within a tier, so each tier reads as the size order does.
    private static readonly IComparer<OrderedRow> ByTier = Comparer<OrderedRow>.Create(
        static (a, b) => a.Tier != b.Tier ? a.Tier.CompareTo(b.Tier) : Then(b.Size.CompareTo(a.Size), a, b));

    /// <summary>
    /// <paramref name="order"/> if a member names it, and otherwise the shipped order. The
    /// preferences file is read with numbers allowed for an enum, so a hand-edited
    /// <c>"StorageOrder": 9</c> arrives as an order nothing can sort by or show.
    /// </summary>
    public static StorageOrder Known(StorageOrder order) =>
        Enum.IsDefined(order) ? order : StorageOrder.Size;

    /// <summary>How <paramref name="order"/> compares two rows: negative when the first goes above the second.</summary>
    public static IComparer<OrderedRow> Comparer(StorageOrder order) => Known(order) switch
    {
        StorageOrder.Name => ByName,
        StorageOrder.Tier => ByTier,
        _ => BySize,
    };

    /// <summary>
    /// The index <paramref name="row"/> is inserted at: after every leading row that goes above it.
    ///
    /// <para>The list is not always in order. A row shrunk by keeping an item stays where it stands,
    /// so the reader's list is not reshuffled for that, and a newcomer then goes before the first row
    /// that should follow it rather than being placed by a search that assumes an order.</para>
    /// </summary>
    /// <param name="listed">Every row already listed, in the order they are listed.</param>
    public static int IndexFor(IEnumerable<OrderedRow> listed, OrderedRow row, StorageOrder order)
    {
        ArgumentNullException.ThrowIfNull(listed);

        var comparer = Comparer(order);
        return listed.TakeWhile(above => comparer.Compare(above, row) < 0).Count();
    }

    private static int Then(int compared, OrderedRow a, OrderedRow b) =>
        compared != 0 ? compared : a.Place.CompareTo(b.Place);
}
