using System.Globalization;
using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What a page can say about one shape on its map, for the map to write in whichever lettering it is
/// drawn in (<see cref="MapLettering"/>).
///
/// <para>The facts rather than the text, because the text depends on the lettering and the room,
/// which the map knows and the page does not: the standard lettering writes a name and its size on one
/// line, and SpaceMonger's writes as many lines of a name, a size in bytes and a date as the shape
/// has room for.</para>
/// </summary>
/// <param name="Name">What the shape is called.</param>
/// <param name="Size">Its size as the page writes it, marked where it is a lower bound.</param>
/// <param name="Bytes">Its size in bytes.</param>
/// <param name="Written">When it was last written, where the page knows.</param>
public readonly record struct MapCaption(string Name, string Size, long Bytes, DateTime? Written = null)
{
    /// <summary>Nothing to write, for a map with no tree.</summary>
    public static MapCaption None { get; } = new(string.Empty, string.Empty, 0);

    /// <summary>
    /// The text written on the shape in <paramref name="lettering"/>, with room for
    /// <paramref name="lines"/> lines of it.
    /// </summary>
    public string Text(MapLettering lettering, int lines)
    {
        if (Name.Length == 0)
        {
            return string.Empty;
        }

        if (lettering != MapLettering.Classic)
        {
            return $"{Name}  {Size}";
        }

        var text = Name;

        if (lines >= 2)
        {
            text += $"\n{Bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";
        }

        if (lines >= 3 && Written is { } written)
        {
            text += $"\n{written.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)}";
        }

        return text;
    }
}
