using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>How much of a Spotify settings file could be read.</summary>
public enum SpotifySettingsReading
{
    /// <summary>There is no settings file, so nothing has moved Spotify's storage.</summary>
    Absent,

    /// <summary>The file was read, and every storage location it names is a full path.</summary>
    Read,

    /// <summary>
    /// The file is there and could not be read: locked, refused, or larger than any settings file
    /// Spotify writes.
    /// </summary>
    Unreadable,

    /// <summary>
    /// The file was read, and it is not UTF-8 text or a storage location in it is not a path
    /// Deguffer can place.
    /// </summary>
    Uninterpretable,
}

/// <summary>
/// Where one Spotify settings file says Spotify keeps its storage.
///
/// <para><b>The file is Spotify's <c>prefs</c>, one <c>key=value</c> per line.</b> A string value
/// is quoted, with a backslash and a quote each escaped by a backslash:
/// <c>storage.location="D:\\Music\\Spotify"</c>. Scripts that edit the file write it that way, and
/// a committed copy of one shows it. Spotify does not document the format.</para>
///
/// <para><b>Two keys are read, and both count.</b> <c>storage.location</c> is where Spotify keeps
/// its storage now, and <c>storage.last-location</c> is where it kept it before. Downloads left at
/// the earlier location are still downloads, so the earlier one is owed the same care.</para>
///
/// <para><b>Anything that cannot be placed is reported, never skipped.</b> Skipping a value would
/// read as "nothing moved", and the provider would then offer a cache the downloads may have been
/// moved into. So a relative path, an unquoted value, an escape nobody has seen Spotify write, or a
/// file that is not UTF-8 text makes the file <see cref="SpotifySettingsReading.Uninterpretable"/>.
/// The locations that could be placed are still returned, because they still name where downloads
/// may be.</para>
///
/// <para><b>The file also holds a saved sign-in.</b> Only the two keys above are taken from it, and
/// nothing else in it is ever kept, logged or shown.</para>
/// </summary>
/// <param name="File">The settings file, named in the sentence the user is shown about it.</param>
/// <param name="Reading">How much of it could be read.</param>
/// <param name="Locations">
/// Every storage location it names that could be placed, once each and normalised. Empty where the
/// file is absent, unreadable or not UTF-8 text.
/// </param>
public sealed record SpotifySettings(
    string File,
    SpotifySettingsReading Reading,
    IReadOnlyList<string> Locations)
{
    public const string LocationKey = "storage.location";

    public const string PreviousLocationKey = "storage.last-location";

    /// <summary>
    /// Far past any settings file Spotify writes, which is some dozens of short lines. A file that
    /// large is not the settings file, and <see cref="BoundedFile"/> reads nothing past it.
    /// </summary>
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>
    /// A decoder that throws on bytes that are not UTF-8, where <see cref="Encoding.UTF8"/> would
    /// replace them. A replacement character inside a path compares unequal to the folder the path
    /// really names, so a location inside the cache would silently stop overlapping it.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether this file says for certain where Spotify's storage is, which may be nowhere special.</summary>
    public bool IsSettled => Reading is SpotifySettingsReading.Absent or SpotifySettingsReading.Read;

    public static SpotifySettings Read(string file)
    {
        if (!LongPath.FileExists(file))
        {
            return new SpotifySettings(file, SpotifySettingsReading.Absent, []);
        }

        if (BoundedFile.Read(file, MaximumBytes) is not { } content)
        {
            return new SpotifySettings(file, SpotifySettingsReading.Unreadable, []);
        }

        return Decode(content) is { } text
            ? Parse(file, text)
            : new SpotifySettings(file, SpotifySettingsReading.Uninterpretable, []);
    }

    /// <summary>The file as text, or null where its bytes are not UTF-8 text.</summary>
    private static string? Decode(ReadOnlyMemory<byte> content)
    {
        try
        {
            var text = StrictUtf8.GetString(content.Span);

            // A NUL is valid UTF-8 and never part of a line Spotify writes. It is what UTF-16 text
            // without a byte order mark looks like when read as UTF-8, and every key in it would
            // then fail to match without a word.
            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: a file saved in an ANSI code page, or UTF-16 with its byte order mark.
            return null;
        }
    }

    private static SpotifySettings Parse(string file, string text)
    {
        var locations = new List<string>();
        var placedEvery = true;

        // Split on the line feed alone, and trim: Spotify writes LF, and a file edited by hand on
        // Windows may have gained a carriage return on every line.
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var split = line.IndexOf('=');

            if (split <= 0 || line[..split].TrimEnd() is not (LocationKey or PreviousLocationKey))
            {
                continue;
            }

            if (Unquote(line[(split + 1)..].TrimStart()) is not { } value)
            {
                placedEvery = false;
                continue;
            }

            // An empty value names no location, which is what an unmoved storage looks like.
            if (value.Length == 0)
            {
                continue;
            }

            // Spotify resolves a relative value against a working directory Deguffer is not, so it
            // has no correct reading here. See LongPath.Configured.
            if (LongPath.Configured(value) is not { } location)
            {
                placedEvery = false;
                continue;
            }

            if (!locations.Contains(location, StringComparer.OrdinalIgnoreCase))
            {
                locations.Add(location);
            }
        }

        return new SpotifySettings(
            file,
            placedEvery ? SpotifySettingsReading.Read : SpotifySettingsReading.Uninterpretable,
            locations);
    }

    /// <summary>The quoted value with its escapes removed, or null where it is not a value this reads.</summary>
    private static string? Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);

        for (var i = 1; i < value.Length - 1; i++)
        {
            var c = value[i];

            // A bare quote inside means the value did not end where the line does.
            if (c == '"')
            {
                return null;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            i++;

            if (i >= value.Length - 1 || value[i] is not ('\\' or '"'))
            {
                return null;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
