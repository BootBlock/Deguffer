namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// How what has been typed narrows a list: case aside, the names that start with it first, then the
/// names that hold it anywhere else, each run in the order it was given.
/// </summary>
public static class NetworkMatch
{
    /// <summary><paramref name="places"/> narrowed to <paramref name="text"/> by name. All of them, in order, for no text.</summary>
    public static IReadOnlyList<T> Narrow<T>(IReadOnlyList<T> places, Func<T, string> name, string text)
    {
        ArgumentNullException.ThrowIfNull(places);

        if (text.Length == 0)
        {
            return places;
        }

        var starting = new List<T>();
        var holding = new List<T>();

        foreach (var place in places)
        {
            var named = name(place);

            if (named.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                starting.Add(place);
            }
            else if (named.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                holding.Add(place);
            }
        }

        return [.. starting, .. holding];
    }

    /// <summary>
    /// Whether the browser's filter keeps a node named <paramref name="name"/> on screen: where its
    /// name holds the filter anywhere, case aside, or where something listed inside it is kept, so
    /// a folder found three levels down is shown with the way to it. Everything is kept for no filter.
    /// </summary>
    public static bool Keeps(string name, string filter, bool keepsAnythingInside) =>
        keepsAnythingInside || filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
