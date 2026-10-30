using System.Text;

namespace Deguffer.Core.Providers;

/// <summary>
/// What one <c>.conanrc</c> says about where Conan's home is, read as Conan reads it:
/// <c>_user_home_from_conanrc_file</c> in <c>conan/internal/paths.py</c>, Conan 2.34.0 (commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>).
///
/// <para><b>Conan's reading is unforgiving, and that is copied rather than tidied.</b> Every line not
/// starting with <c>#</c> is split on <c>=</c> and unpacked into a key and a value, so a blank line,
/// a line with no <c>=</c> or one with two raises a <c>ValueError</c> nothing catches, and every Conan
/// command then fails. Neither half is trimmed, so <c>conan_home = x</c> sets the key
/// <c>"conan_home "</c>, which is not the one looked up. An empty value is false in Python, so
/// <c>conan_home=</c> falls through to <c>CONAN_HOME</c> exactly as a missing key does.</para>
///
/// <para><b>Only ASCII is read.</b> Python opens the file in its locale's encoding, which differs
/// between machines and between Python versions, so the same bytes outside ASCII can name two
/// different folders. Guessing one would let Deguffer check a home Conan does not use, and Conan
/// creates its home when it is missing.</para>
/// </summary>
internal static class ConanRc
{
    public const string FileName = ".conanrc";

    private const string HomeKey = "conan_home";

    /// <summary>Far past a file of one or two settings, and small enough to read whole.</summary>
    private const int MaximumBytes = 64 * 1024;

    /// <summary>The one <c>.conanrc</c> at <paramref name="file"/>, known to be there.</summary>
    public static ConanRcReading Read(string file)
    {
        // Verbatim, so that a byte order mark, which Python's locale reading keeps, is outside ASCII
        // below rather than silently dropped.
        if (BoundedFile.ReadVerbatim(file, MaximumBytes) is not { } content)
        {
            // Conan reads a file it cannot open as naming nothing. What kept Deguffer out need not
            // keep Conan out, though, and Conan reads a file of any size, so which home it settles
            // on is not known.
            return new ConanRcReading.Unusable(
                $"Deguffer could not read {file}, which tells Conan where its home is, or it is larger than "
                + $"{MaximumBytes / 1024} KB, which no .conanrc needs to be.");
        }

        var bytes = content.Span;

        if (bytes.ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            return new ConanRcReading.Unusable(
                $"{file} holds characters outside ASCII, and Conan reads it in an encoding that "
                + "depends on how Python was set up, so which home it names cannot be known here.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Lines(Encoding.ASCII.GetString(bytes)).Where(line => !line.StartsWith('#')))
        {
            if (line.Split('=') is not [var key, var value])
            {
                return new ConanRcReading.Unusable(
                    $"Conan stops with an error on reading {file}, because a line in it is not one "
                    + "name and one value.");
            }

            values[key] = value;
        }

        if (!values.TryGetValue(HomeKey, out var home) || home.Length == 0)
        {
            return new ConanRcReading.Silent();
        }

        // Conan's own test for a value relative to the file: "./" or ".\" at the start, or "..".
        var relative = home.StartsWith("./", StringComparison.Ordinal)
            || home.StartsWith(".\\", StringComparison.Ordinal)
            || home.StartsWith("..", StringComparison.Ordinal);

        return new ConanRcReading.Names(relative ? Path.Combine(Path.GetDirectoryName(file)!, home) : home);
    }

    /// <summary>
    /// Python's <c>str.splitlines</c> over ASCII text: a break is a line feed, a carriage return, the
    /// two together, a vertical tab, a form feed or a file, group or record separator, and a break at
    /// the very end makes no empty line after it.
    /// </summary>
    private static IEnumerable<string> Lines(string text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e'))
            {
                continue;
            }

            yield return text[start..i];

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}

/// <summary>What a <c>.conanrc</c> came to.</summary>
internal abstract record ConanRcReading
{
    /// <summary>
    /// The file names a home, as Conan would go on to expand it: joined to the file's folder where it
    /// is relative, and otherwise as written.
    /// </summary>
    public sealed record Names(string Home) : ConanRcReading;

    /// <summary>The file names no home, so Conan goes on to <c>CONAN_HOME</c>.</summary>
    public sealed record Silent : ConanRcReading;

    /// <summary>Which home the file leaves Conan in cannot be known, for the reason given.</summary>
    public sealed record Unusable(string Why) : ConanRcReading;
}
