namespace Deguffer.Core.Duplicates;

/// <summary>
/// The smallest and the largest length a file may have to be searched, each inclusive, either
/// absent. An empty file is never searched whatever this says (§7.4).
/// </summary>
public readonly record struct SizeRange(long? Smallest = null, long? Largest = null)
{
    /// <summary>Why the range cannot be searched, or null where it can.</summary>
    public string? WhyRefused =>
        Smallest < 0 || Largest < 0
            ? "A size cannot be less than nothing."
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
/// case, with or without its leading dot as the user typed it. A file with no extension has none of
/// the listed ones.
/// </summary>
public sealed record ExtensionFilter
{
    public static readonly ExtensionFilter Any = new(ExtensionFilterMode.Any, []);

    private readonly HashSet<string> _extensions;

    public ExtensionFilter(ExtensionFilterMode mode, IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        Mode = mode;
        _extensions = new HashSet<string>(
            extensions.Select(Normalised).Where(extension => extension.Length > 1),
            StringComparer.OrdinalIgnoreCase);
        Extensions = [.. _extensions.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public ExtensionFilterMode Mode { get; }

    /// <summary>The extensions listed, each with its leading dot, in order.</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>Whether a file named <paramref name="name"/> is searched.</summary>
    public bool Admits(string name) => Mode switch
    {
        ExtensionFilterMode.OnlyThese => _extensions.Contains(Path.GetExtension(name)),
        ExtensionFilterMode.SkipThese => !_extensions.Contains(Path.GetExtension(name)),
        _ => true,
    };

    public bool Equals(ExtensionFilter? other) =>
        other is not null && Mode == other.Mode && _extensions.SetEquals(other._extensions);

    public override int GetHashCode() => HashCode.Combine(Mode, Extensions.Count);

    private static string Normalised(string extension)
    {
        var trimmed = extension.Trim().TrimStart('*');

        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }
}
