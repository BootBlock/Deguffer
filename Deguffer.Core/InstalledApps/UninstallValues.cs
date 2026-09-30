namespace Deguffer.Core.InstalledApps;

/// <summary>
/// An entry's values, each as the type it was written as (§7.3).
///
/// <para>A value of an unexpected type answers null from the accessor that expected another type,
/// and the entry stands. The earlier tool this page replaces cast every value inside one block, so
/// one <c>EstimatedSize</c> written as a string dropped the whole entry from the list.</para>
/// </summary>
public sealed class UninstallValues
{
    private readonly IReadOnlyDictionary<string, object> _values;

    /// <param name="values">
    /// Value names to data as the registry returns it: a string for <c>REG_SZ</c> and an expanded
    /// <c>REG_EXPAND_SZ</c>, an <see cref="int"/> for <c>REG_DWORD</c>, a <see cref="long"/> for
    /// <c>REG_QWORD</c>, and whatever else for the rest.
    /// </param>
    public UninstallValues(IReadOnlyDictionary<string, object> values) =>
        _values = values.ToDictionary(StringComparer.OrdinalIgnoreCase);

    public static UninstallValues None { get; } = new(new Dictionary<string, object>());

    /// <summary>A string value with its surrounding space removed, or null where it is empty or not a string.</summary>
    public string? Text(string name) =>
        _values.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>
    /// Whether a value says anything: false only where it is missing or is a string of nothing but
    /// space. A value of another type says something even where no accessor here can read it.
    /// </summary>
    public bool IsSet(string name) =>
        _values.TryGetValue(name, out var value) && !(value is string text && string.IsNullOrWhiteSpace(text));

    /// <summary>A number value, or null where it is missing or not a number.</summary>
    public long? Number(string name) => _values.TryGetValue(name, out var value)
        ? value switch
        {
            int dword => dword,
            long qword => qword,
            _ => null,
        }
        : null;

    /// <summary>Whether a number value is exactly 1, the way Windows reads its flags.</summary>
    public bool Flag(string name) => Number(name) == 1;

    /// <summary>
    /// Whether this holds exactly what <paramref name="other"/> holds. Asked immediately before an
    /// entry is changed, so a key rewritten since the user chose it is refused rather than acted on.
    /// </summary>
    public bool SameAs(UninstallValues other) =>
        _values.Count == other._values.Count
        && _values.All(pair => other._values.TryGetValue(pair.Key, out var value) && Same(pair.Value, value));

    private static bool Same(object left, object right) => (left, right) switch
    {
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
        _ => Equals(left, right),
    };
}
