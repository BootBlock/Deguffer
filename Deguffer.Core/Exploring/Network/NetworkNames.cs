using System.Buffers;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// How a network place's path is built from its parts and read back into them. Paths here are in
/// display form, <c>\\server\share\folder</c>: what the box shows and what the page's check is
/// handed, which asks in the extended form itself (§6.3).
/// </summary>
public static class NetworkNames
{
    private const char Separator = '\\';

    private const string Separators = @"\\";

    /// <summary>The longest DNS name, which is the longest a computer typed by name or address can be.</summary>
    private const int LongestName = 253;

    /// <summary>
    /// The characters no computer name may hold: a separator would make it a path, and the rest are
    /// refused in a UNC host name. A colon is among them, so an IPv6 address is typed in its
    /// <c>ipv6-literal.net</c> spelling, as Windows itself requires in a UNC path.
    /// </summary>
    private static readonly SearchValues<char> NotInAName = SearchValues.Create("\\/:*?\"<>| \t");

    /// <summary><c>\\name</c>, the path a computer is reached at.</summary>
    public static string Computer(string name) => Separators + name;

    /// <summary><paramref name="name"/> one level inside <paramref name="parent"/>.</summary>
    public static string Inside(string parent, string name) => parent.TrimEnd(Separator) + Separator + name;

    /// <summary>The last name in <paramref name="path"/>, which is what a place is called in the tree.</summary>
    public static string LastName(string path)
    {
        var trimmed = path.TrimEnd(Separator);
        var at = trimmed.LastIndexOf(Separator);

        return at < 0 ? trimmed : trimmed[(at + 1)..];
    }

    /// <summary>The computer <paramref name="path"/> is on, or null where it is not a share path.</summary>
    public static string? ServerOf(string path)
    {
        if (!IsUnc(path))
        {
            return null;
        }

        var rest = path.AsSpan(2);
        var end = rest.IndexOf(Separator);

        return (end < 0 ? rest : rest[..end]).ToString();
    }

    /// <summary>The share <paramref name="path"/> is in, as <c>\\server\share</c>, or null where it names no share.</summary>
    public static string? ShareOf(string path)
    {
        if (ServerOf(path) is not { } server)
        {
            return null;
        }

        var rest = path.AsSpan(2 + server.Length);

        if (rest.Length < 2 || rest[0] != Separator)
        {
            return null;
        }

        var share = rest[1..];
        var end = share.IndexOf(Separator);

        return Inside(Computer(server), (end < 0 ? share : share[..end]).ToString());
    }

    /// <summary>
    /// Whether <paramref name="target"/> is <paramref name="place"/> or somewhere inside it, case
    /// aside, which is how the tree finds its way back to the place it last showed.
    /// </summary>
    public static bool Leads(string place, string target)
    {
        var trimmed = place.TrimEnd(Separator);

        return target.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            || (target.Length > trimmed.Length
                && target.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                && target[trimmed.Length] == Separator);
    }

    /// <summary>
    /// The computer <paramref name="typed"/> names, as it is added to the tree, or null where it
    /// names none. A computer is typed as <c>nas</c>, <c>nas.local</c> or <c>192.0.2.10</c>, and
    /// leading separators and a trailing one are forgiven, because <c>\\nas</c> is how a computer is
    /// written everywhere else in Windows.
    /// </summary>
    public static string? ComputerName(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);

        var name = typed.Trim().TrimStart(Separator).TrimEnd(Separator);

        return name.Length is 0 or > LongestName || name.AsSpan().ContainsAny(NotInAName) || name.StartsWith('.') ? null : name;
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with a computer: two separators and a name. A device
    /// path (<c>\\?\</c>, <c>\\.\</c>) starts with two separators and names no computer.
    /// </summary>
    private static bool IsUnc(string path) =>
        path.Length > 2 && path[0] == Separator && path[1] == Separator && path[2] is not ('?' or '.' or Separator);
}
