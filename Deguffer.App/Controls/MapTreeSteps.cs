namespace Deguffer.App.Controls;

/// <summary>
/// Where a step through the map's tree lands from a shape's peer (<see cref="MapShapeAutomationPeer"/>):
/// its sibling one way or the other, or a folder's first or last shape.
///
/// <para>A search of the window walks a folder's shapes one sibling at a time. The framework's own step
/// finds the peer it starts from by reading its parent's children from the first, one call across the
/// boundary each, so a walk through a folder of n shapes costs n squared of them, and a folder of tens of
/// thousands on the keyboard's path stops the window answering. A peer knows its own place among its
/// folder's shapes, so the step reads the shapes either side of that place and nothing else.</para>
/// </summary>
internal static class MapTreeSteps
{
    /// <summary>
    /// The sibling one step from <paramref name="self"/>, which is at <paramref name="index"/> among
    /// <paramref name="siblings"/>: the next where <paramref name="forward"/>, otherwise the previous.
    /// </summary>
    /// <param name="after">What follows the last sibling in the tree, asked only when a step passes it.</param>
    /// <returns>
    /// Null where nothing lies that way, or where <paramref name="self"/> is not at its place in
    /// <paramref name="siblings"/>, as a peer kept from a folder collapsed since, or from another drawing, is
    /// not: it is in the tree no longer, so it has no siblings there.
    /// </returns>
    public static T? Sibling<T>(IReadOnlyList<T> siblings, int index, T self, bool forward, Func<T?> after)
        where T : class
    {
        if (index < 0 || index >= siblings.Count || !ReferenceEquals(siblings[index], self))
        {
            return null;
        }

        var next = forward ? index + 1 : index - 1;

        return next < 0 ? null
            : next < siblings.Count ? siblings[next]
            : after();
    }

    /// <summary>The first of <paramref name="children"/> where <paramref name="first"/>, otherwise the last, or null where there are none.</summary>
    public static T? Child<T>(IReadOnlyList<T> children, bool first)
        where T : class =>
        children.Count == 0 ? null : children[first ? 0 : children.Count - 1];
}
