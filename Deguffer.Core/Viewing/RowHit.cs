namespace Deguffer.Core.Viewing;

/// <summary>What an element between a gesture's source and the list says about the gesture. See <see cref="RowHit"/>.</summary>
public enum RowPart
{
    /// <summary>Text, an icon or a panel inside a row, which passes the gesture on to the row.</summary>
    Content,

    /// <summary>A button, which acts on the gesture itself.</summary>
    Button,

    /// <summary>The row's own container.</summary>
    Row,
}

/// <summary>
/// The row a gesture in a list landed on, walking up from the element under it.
///
/// <para>The walk stops at a button. A button inside a row takes the gesture as its own: the list
/// commits no selection for a press on it, and the row's own key handlers, registered past the handled
/// flag, would otherwise see an Enter the button had already turned into a click. Measured on the
/// Memory page, where one keypress on a row's chevron descended two levels: the button's click opened
/// the row, and the row's Enter then opened the container the first open had rebound.</para>
/// </summary>
public static class RowHit
{
    /// <summary>
    /// The first of <paramref name="fromSource"/> that is a row, or null where the walk meets a button
    /// first or meets no row at all.
    /// </summary>
    /// <param name="fromSource">The element under the gesture, then each element above it in turn.</param>
    /// <param name="part">What each element is.</param>
    public static T? Find<T>(IEnumerable<T> fromSource, Func<T, RowPart> part)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fromSource);
        ArgumentNullException.ThrowIfNull(part);

        foreach (var element in fromSource)
        {
            switch (part(element))
            {
                case RowPart.Row:
                    return element;
                case RowPart.Button:
                    return null;
            }
        }

        return null;
    }
}
