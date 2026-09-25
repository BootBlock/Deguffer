using System.Runtime.InteropServices;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The 8.3 alias of a real path, for tests that prove a comparison survives one. Windows sets the
/// per-user <c>TEMP</c> variable to this form on a profile whose folder name exceeds eight
/// characters, so it is the form a program in <c>%TEMP%</c> ordinarily holds.
/// </summary>
public static class ShortPath
{
    /// <summary>
    /// The 8.3 alias for <paramref name="path"/>, or null where this volume creates none.
    ///
    /// Asked of Windows rather than constructed, because whether short names exist at all is a
    /// per-volume setting and the alias's digits depend on what else is in the folder.
    /// </summary>
    public static string? Of(string path)
    {
        var length = GetShortPathName(path, null, 0);

        if (length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        var written = GetShortPathName(path, buffer, length);
        var shortForm = written > 0 && written < length ? new string(buffer, 0, (int)written) : null;

        return string.Equals(shortForm, path, StringComparison.Ordinal) ? null : shortForm;
    }

    // DllImport rather than LibraryImport, which needs AllowUnsafeBlocks; the test project does not
    // enable it and one fixture helper is a poor reason to.
    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, [Out] char[]? buffer, uint length);
}
