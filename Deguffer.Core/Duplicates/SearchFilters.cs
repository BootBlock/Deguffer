using System.Buffers;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The smallest and the largest length a file may have to be searched, each inclusive, either
/// absent. An empty file is never searched whatever this says (§7.4).
/// </summary>
public readonly record struct SizeRange(long? Smallest = null, long? Largest = null)
{
    /// <summary>
    /// Why the range cannot be searched, or null where it can. A largest size of nothing admits only
    /// empty files, which are never matched, so it is refused as a range that admits no file.
    /// </summary>
    public string? WhyRefused =>
        Smallest < 0 || Largest < 0
            ? "A size cannot be less than nothing."
            : Largest < 1
                ? "The largest size is less than one byte. Empty files are never matched, so no file could be searched."
                : Smallest > Largest
                    ? "The smallest size is larger than the largest, so no file could be searched."
                    : null;

    public bool Admits(long length) => length >= (Smallest ?? 0) && length <= (Largest ?? long.MaxValue);
}

/// <summary>Which way an <see cref="ExtensionFilter"/> reads its list.</summary>
public enum ExtensionFilterMode
{
    /// <summary>Every extension is searched, and the list is not read.</summary>
    Any,

    /// <summary>Only files with one of the extensions listed.</summary>
    OnlyThese,

    /// <summary>Every file except those with one of the extensions listed.</summary>
    SkipThese,
}

/// <summary>
/// The extensions to search, or the ones to skip. Each is compared ordinally and without regard to
/// case, written with or without its leading dot, or as <c>*.jpg</c>. A file with no extension has
/// none of the listed ones, and a blank entry names nothing.
///
/// <para><b>An entry that is not one extension is refused, not dropped</b>
/// (<see cref="WhyRefused"/>). A wildcard such as <c>*</c> or <c>*.*</c> names every file rather
/// than an extension; an entry with a dot inside, such as <c>tar.gz</c>, is never a file's
/// extension, which is what follows its last dot; and a character no file name holds names nothing.
/// Dropped, each would leave a filter that searched what the user did not ask for, or, as the only
/// entry of a list to search, a search that could find nothing. A list to search that names no
/// extension is refused for the same reason.</para>
/// </summary>
public sealed record ExtensionFilter
{
    public static readonly ExtensionFilter Any = new(ExtensionFilterMode.Any, []);

    /// <summary>What an extension never holds after its leading dot: another dot, a wildcard, or anything a name cannot.</summary>
    private static readonly SearchValues<char> NotInAnExtension = SearchValues.Create([.. Path.GetInvalidFileNameChars(), '.']);

    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionFilter(ExtensionFilterMode mode, IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        Mode = mode;
        string? notAnExtension = null;

        foreach (var entry in extensions.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            if (Normalised(entry) is { } extension)
            {
                _extensions.Add(extension);
            }
            else
            {
                notAnExtension ??= entry.Trim();
            }
        }

        Extensions = [.. _extensions.Order(StringComparer.OrdinalIgnoreCase)];
        WhyRefused = mode switch
        {
            ExtensionFilterMode.Any => null,
            _ when notAnExtension is not null =>
                $"'{notAnExtension}' is not an extension. Write each as the letters after a file name's last dot, such as jpg or .png.",
            ExtensionFilterMode.OnlyThese when _extensions.Count == 0 =>
                "Name at least one extension to search, or search every extension.",
            _ => null,
        };
    }

    public ExtensionFilterMode Mode { get; }

    /// <summary>The extensions listed, each with its leading dot, in order.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Why the filter cannot be searched with, or null where it can: an entry that is not one
    /// extension, or a list to search that names none. Never refused where <see cref="Mode"/> is
    /// <see cref="ExtensionFilterMode.Any"/>, which does not read the list.
    /// </summary>
    public string? WhyRefused { get; }

    /// <summary>Whether a file named <paramref name="name"/> is searched.</summary>
    public bool Admits(string name) => Mode switch
    {
        ExtensionFilterMode.OnlyThese => _extensions.Contains(Path.GetExtension(name)),
        ExtensionFilterMode.SkipThese => !_extensions.Contains(Path.GetExtension(name)),
        _ => true,
    };

    public bool Equals(ExtensionFilter? other) =>
        other is not null
        && Mode == other.Mode
        && _extensions.SetEquals(other._extensions)
        && WhyRefused == other.WhyRefused;

    public override int GetHashCode() => HashCode.Combine(Mode, Extensions.Count);

    /// <summary>The extension <paramref name="entry"/> names, with its leading dot, or null where it names none.</summary>
    private static string? Normalised(string entry)
    {
        var trimmed = entry.Trim();

        // How a shell pattern writes an extension; any other wildcard is not one.
        if (trimmed.StartsWith("*.", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        var letters = trimmed.StartsWith('.') ? trimmed[1..] : trimmed;

        return letters.Length > 0 && letters.AsSpan().IndexOfAny(NotInAnExtension) < 0 ? "." + letters : null;
    }
}
