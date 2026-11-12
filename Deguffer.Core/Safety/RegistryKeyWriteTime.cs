using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Safety;

/// <summary>
/// When a registry key was last written, which <see cref="RegistryKey"/> does not expose. Windows sets
/// it when the key is created and whenever a value or subkey of it changes, from the system clock.
/// </summary>
internal static partial class RegistryKeyWriteTime
{
    private const int Success = 0;

    /// <summary>
    /// When <paramref name="key"/> was last written, in UTC, or null where Windows would not say.
    ///
    /// <para>A file time at or below zero is not a time. Zero is 1601, which a caller comparing ages
    /// would read as "certainly old" rather than "there is nothing here to date", and a negative one
    /// is what <see cref="DateTime.FromFileTimeUtc"/> throws on.</para>
    /// </summary>
    public static DateTime? Of(RegistryKey key) =>
        RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var written) == Success && written > 0
            ? DateTime.FromFileTimeUtc(written)
            : null;

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static partial int RegQueryInfoKey(
        SafeRegistryHandle key,
        nint className,
        nint classLength,
        nint reserved,
        nint subKeys,
        nint longestSubKey,
        nint longestClass,
        nint values,
        nint longestValueName,
        nint longestValue,
        nint securityDescriptor,
        out long lastWriteTime);
}
