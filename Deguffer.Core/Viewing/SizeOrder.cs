namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a row joins a list that §7 sorts by size, largest first, while the list is still filling.
///
/// <para>Rows arrive as their providers finish (§5.5), so each is inserted where it belongs rather
/// than the list being sorted once the last one lands, which would reshuffle it under the reader.</para>
/// </summary>
public static class SizeOrder
{
    /// <summary>
    /// The index a row of <paramref name="size"/> is inserted at: after every leading row larger than
    /// it, and after every leading row of its size whose provider comes before its own.
    ///
    /// <para><b>A tie goes by the provider's place, not by when the row arrived.</b> Providers are
    /// planned several at once and finish in whatever order their work takes, so rows of equal size,
    /// most of all the many that found nothing, would otherwise stand in a different order after every
    /// scan.</para>
    ///
    /// <para>The list is not always in order. A row shrunk by keeping an item stays where it stands,
    /// so the reader's list is not reshuffled for that, and a newcomer then goes before the first row
    /// that should follow it rather than being placed by a search that assumes an order.</para>
    /// </summary>
    /// <param name="listed">
    /// The size of every row already listed, and the place of its provider, in the order they are
    /// listed.
    /// </param>
    /// <param name="place">The place of the row's provider, as <see cref="Execution.CleanupPlanner.PlaceOf"/> gives it.</param>
    public static int IndexFor(IEnumerable<(long Size, int Place)> listed, long size, int place)
    {
        ArgumentNullException.ThrowIfNull(listed);

        return listed.TakeWhile(row => row.Size > size || (row.Size == size && row.Place < place)).Count();
    }
}
