using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One value from a settings file in the INI form the emulators built on SimpleIni write: a
/// <c>[Section]</c> line, then <c>Key = Value</c> lines, compared without regard to case.
///
/// <para>PCSX2 and DuckStation both name their cache folder this way, and both use whatever it says,
/// so a layout reads it rather than assuming the default.</para>
/// </summary>
internal static class IniSetting
{
    /// <summary>
    /// The folder <paramref name="key"/> names, absolute or relative to <paramref name="root"/> as
    /// both emulators resolve it, or <paramref name="fallback"/> in <paramref name="root"/> where the
    /// value is missing, empty or unreadable, which is the folder each falls back to as well. Null
    /// where the path is one Deguffer will not act on.
    /// </summary>
    public static string? Folder(string root, string file, string section, string key, string fallback, int maximumBytes)
    {
        var configured = Read(file, section, key, maximumBytes) is { Length: > 0 } value ? value : fallback;

        return Path.IsPathFullyQualified(configured)
            ? LongPath.Configured(configured)
            : LongPath.Configured(Path.Combine(root, configured));
    }

    /// <summary>
    /// The value of <paramref name="key"/> in <paramref name="section"/>, trimmed, or null where the
    /// file, the section or the key is missing, or the file is larger than
    /// <paramref name="maximumBytes"/> or cannot be read.
    /// </summary>
    public static string? Read(string file, string section, string key, int maximumBytes)
    {
        if (BoundedFile.Read(file, maximumBytes) is not { } content)
        {
            return null;
        }

        var inSection = false;

        foreach (var raw in Encoding.UTF8.GetString(content.Span).Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection
                && line.IndexOf('=') is > 0 and var equals
                && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return null;
    }
}
