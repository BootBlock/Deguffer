using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Startup;

/// <summary>
/// One registry value as it is stored: its type and its data, with nothing expanded. §7.5's
/// checks compare what was there before a change with what is there after, so a value is compared
/// as the bytes Windows keeps, never as what a reader made of them.
/// </summary>
/// <param name="Data">
/// What <see cref="RegistryKey.GetValue(string?, object?, RegistryValueOptions)"/> returned with
/// expansion turned off: a <see cref="string"/>, a <see cref="string"/> array, a <see cref="byte"/>
/// array, an <see cref="int"/> or a <see cref="long"/>.
/// </param>
/// <param name="Expanded">
/// What Windows runs for a <c>REG_EXPAND_SZ</c>: its text with the environment expanded, as the
/// registry expands it on an ordinary read. Null for every other type. Not part of
/// <see cref="SameAs"/>, which compares what is stored.
/// </param>
public sealed record StoredValue(RegistryValueKind Kind, object Data, string? Expanded = null)
{
    /// <summary>The data as text, where the value is a string of either kind.</summary>
    public string? Text => Kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? Data as string : null;

    /// <summary>The data as bytes, where the value is binary.</summary>
    public byte[]? Bytes => Kind is RegistryValueKind.Binary ? Data as byte[] : null;

    /// <summary>Whether <paramref name="other"/> is the same type holding the same data, element by element.</summary>
    public bool SameAs(StoredValue? other) =>
        other is not null && Kind == other.Kind && RegistryData.Same(Data, other.Data);
}
