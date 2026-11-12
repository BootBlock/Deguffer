namespace Deguffer.Core.Native;

/// <summary>
/// The Windows multi-string: each string ended by a null, and the set ended by an empty one, as the
/// configuration manager writes a device list and a string-list property.
/// </summary>
internal static class MultiString
{
    /// <summary>The strings before the first empty one. Anything after it is not part of the set.</summary>
    internal static IReadOnlyList<string> Split(ReadOnlySpan<char> multiString)
    {
        var strings = new List<string>();

        while (multiString.IndexOf('\0') is var end and > 0)
        {
            strings.Add(multiString[..end].ToString());
            multiString = multiString[(end + 1)..];
        }

        return strings;
    }
}
