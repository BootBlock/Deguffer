using System.Buffers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// How a network place's path is built from its parts and read back into them. Paths here are in
/// display form, <c>\\server\share\folder</c>: what the box shows and what the page's check is
/// handed, which asks in the extended form itself (§6.3). Whether a path is a share at all is
/// <see cref="LongPath.IsShare"/>'s to say.
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

    /// <summary>Either separator, which Windows reads alike in a share path.</summary>
    private static readonly SearchValues<char> EitherSeparator = SearchValues.Create("\\/");

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
    public static string? ServerOf(string path) => Names(path) is { } names ? names.Server : null;

    /// <summary>The share <paramref name="path"/> is in, as <c>\\server\share</c>, or null where it names no share.</summary>
    public static string? ShareOf(string path) =>
        Names(path) is { Share: { } share } names ? Inside(Computer(names.Server), share) : null;

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

    /// <summary>The computer and the share <paramref name="path"/> names, in either spelling, or null where it is no share path.</summary>
    private static (string Server, string? Share)? Names(string path)
    {
        var shown = LongPath.Display(path);

        if (!LongPath.IsShare(shown))
        {
            return null;
        }

        var rest = shown.AsSpan(2);
        var serverEnd = rest.IndexOfAny(EitherSeparator);

        if (serverEnd < 0)
        {
            return (rest.ToString(), null);
        }

        // A third separator names no computer at all.
        if (serverEnd == 0)
        {
            return null;
        }

        var share = rest[(serverEnd + 1)..];
        var shareEnd = share.IndexOfAny(EitherSeparator);
        share = shareEnd < 0 ? share : share[..shareEnd];

        return (rest[..serverEnd].ToString(), share.IsEmpty ? null : share.ToString());
    }
}
