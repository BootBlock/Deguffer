namespace Deguffer.Core.Viewing;

/// <summary>
/// Where a row joins a list that §7 sorts by size, largest first, while the list is still filling.
///
/// <para>Rows arrive one provider at a time (§5.5), so each is inserted where it belongs rather than
/// the list being sorted once the last one lands, which would reshuffle it under the reader.</para>
/// </summary>
public static class SizeOrder
{
    /// <summary>
    /// The index a row of <paramref name="size"/> is inserted at: after every leading row at least as
    /// large, so rows of equal size keep the order they arrived in.
    ///
    /// <para>The list is not always in order. A row shrunk by keeping an item stays where it stands,
    /// so the reader's list is not reshuffled for that, and a newcomer then goes before the first row
    /// smaller than itself rather than being placed by a search that assumes an order.</para>
    /// </summary>
    /// <param name="sizes">The size of every row already listed, in the order they are listed.</param>
    public static int IndexFor(IEnumerable<long> sizes, long size)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        return sizes.TakeWhile(listed => listed >= size).Count();
    }
}
