using System.Globalization;
using Deguffer.Core.Configuration;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What a page can say about one shape on its map, for the map to write in whichever lettering it is
/// drawn in (<see cref="MapLettering"/>).
///
/// <para>The facts rather than the text, because the text depends on the lettering and the room,
/// which the map knows and the page does not: the standard lettering writes a name and its size on one
/// line, and SpaceMonger's writes as many lines of a name, a size and a date as the shape has room
/// for.</para>
///
/// <para>Both letterings write the size in the unit that suits it, as the rest of the page does,
/// rather than SpaceMonger's exact count of bytes: a dozen digits in a box are read as a length, not a
/// number, and the reader comparing two boxes needs the magnitude at a glance.</para>
/// </summary>
/// <param name="Name">What the shape is called.</param>
/// <param name="Size">Its size as the page writes it, marked where it is a lower bound.</param>
/// <param name="Written">When it was last written, where the page knows.</param>
public readonly record struct MapCaption(string Name, string Size, DateTime? Written = null)
{
    /// <summary>Nothing to write, for a map with no tree.</summary>
    public static MapCaption None { get; } = new(string.Empty, string.Empty);

    /// <summary>
    /// The text written on the shape in <paramref name="lettering"/>, with room for
    /// <paramref name="lines"/> lines of it, with its size where <paramref name="detail"/> asks for
    /// sizes (<see cref="MapDetail.Sizes"/>): after the name, or under SpaceMonger's lettering a line of
    /// its own between the name and the date, whose place the date takes where there is none.
    /// </summary>
    public string Text(MapLettering lettering, int lines, MapDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var sizes = detail.Sizes;

        if (Name.Length == 0)
        {
            return string.Empty;
        }

        if (lettering != MapLettering.Classic)
        {
            return sizes ? $"{Name}  {Size}" : Name;
        }

        var text = Name;
        var used = 1;

        if (sizes && lines > used)
        {
            text += $"\n{Size}";
            used++;
        }

        if (lines > used && Written is { } written)
        {
            text += $"\n{written.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)}";
        }

        return text;
    }
}
