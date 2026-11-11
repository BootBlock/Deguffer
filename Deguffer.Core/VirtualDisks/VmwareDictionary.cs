using System.Globalization;
using System.Text;
using Deguffer.Core.Providers;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A file in the dictionary format VMware Workstation keeps its records in: a machine's <c>.vmx</c> and
/// <c>.vmsd</c>, the library's <c>inventory.vmls</c> and <c>preferences.ini</c>.
///
/// <para>Each line is <c>key = "value"</c>, and a line opening with <c>#</c> is a comment. A
/// <c>.encoding</c> line names the text's encoding, which libvirt's reader follows, and community copies
/// of <c>inventory.vmls</c> show <c>windows-1252</c> as well as UTF-8. A character a value cannot hold as
/// it is is written as <c>|</c> and two hex digits, <c>|22</c> for a quotation mark, and every value is
/// decoded, because libvirt's reader (<c>src/vmx/vmx.h</c>) and community copies of
/// <c>preferences.ini</c> show the escape in more than one file. VMware documents none of this.</para>
/// </summary>
internal static class VmwareDictionary
{
    /// <summary>A machine's records are a few kilobytes, and a large library's a few hundred. Anything larger is not them.</summary>
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>
    /// The file's keys and values, keys compared without regard to case as VMware does, or null where the
    /// file could not be read or is not text.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Read(string path) =>
        BoundedFile.Read(path, MaximumBytes) is { } content ? Parse(content.Span) : null;

    internal static IReadOnlyDictionary<string, string>? Parse(ReadOnlySpan<byte> content)
    {
        if (Decode(content, ".encoding") is not { } text)
        {
            return null;
        }

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#' || line.IndexOf('=') is not (> 0 and var equals))
            {
                continue;
            }

            var value = line[(equals + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            entries[line[..equals].Trim()] = Unescape(value);
        }

        return entries;
    }

    /// <summary>
    /// The text in the encoding the line keyed <paramref name="encodingKey"/> names, or else UTF-8, or else
    /// Windows-1252 for an older file that names none. Null where a named encoding is unknown, or the bytes
    /// are not that text, because a path decoded in the wrong encoding names a file that is not there.
    /// </summary>
    /// <param name="encodingKey">
    /// The key that names the encoding: <c>.encoding</c> in a dictionary file, and <c>encoding</c> in a
    /// disk's descriptor.
    /// </param>
    internal static string? Decode(ReadOnlySpan<byte> content, string encodingKey)
    {
        // Latin-1 maps every byte to one character, so the line naming the encoding is found whatever the
        // encoding turns out to be: the line itself is ASCII.
        var named = Encoding.Latin1.GetString(content)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.IndexOf('=') is > 0 and var at
                && line[..at].Trim().Equals(encodingKey, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (named is not null)
            {
                var name = named[(named.IndexOf('=') + 1)..].Trim().Trim('"');

                var encoding = CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);

                return encoding.WebName == Encoding.UTF8.WebName
                    ? BoundedFile.StrictUtf8.GetString(content)
                    : encoding.GetString(content);
            }

            return BoundedFile.StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return named is null ? CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(content) : null;
        }
        catch (ArgumentException)
        {
            // An encoding name Windows does not know: the text cannot be read as its writer meant it.
            return null;
        }
    }

    /// <summary><paramref name="value"/> with each <c>|XX</c> replaced by the character it stands for.</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('|'))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '|'
                && i + 2 < value.Length
                && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                text.Append((char)code);
                i += 2;
            }
            else
            {
                text.Append(value[i]);
            }
        }

        return text.ToString();
    }
}
