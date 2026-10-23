namespace Deguffer.Core.Safety;

/// <summary>
/// Whether two registry values' data are the same, element by element. One definition for every
/// check that a value is still what it was when the user chose from it, an uninstall entry (§7.3) and
/// a startup entry (§7.5) among them.
/// </summary>
public static class RegistryData
{
    /// <summary>
    /// Whether <paramref name="left"/> and <paramref name="right"/> hold the same data. Arrays are
    /// compared by their elements, because the registry hands back a new array on every read.
    /// </summary>
    public static bool Same(object left, object right) => (left, right) switch
    {
        (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
        (string[] a, string[] b) => a.SequenceEqual(b, StringComparer.Ordinal),
        _ => Equals(left, right),
    };
}
