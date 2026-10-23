using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>What a shortcut points at, as the shortcut records it.</summary>
/// <param name="Target">The file it opens, or null where it names none, as a shortcut to a shell folder does.</param>
/// <param name="Arguments">What it passes the file.</param>
public sealed record ShortcutTarget(string? Target, string Arguments);

/// <summary>
/// Reads a shortcut, behind a seam so a Startup folder can be proved against shortcuts a test
/// describes rather than ones the shell has to write (G1).
/// </summary>
public interface IShortcutReader
{
    /// <summary>What <paramref name="path"/> points at, or null where it will not load as a shortcut.</summary>
    ShortcutTarget? Read(string path);
}

/// <summary>
/// Reads a <c>.lnk</c> through the shell's own <c>IShellLinkW</c>, which is the documented reader of
/// the format (§5.1 for a file: the format's owner reads it).
///
/// <para><b>Loaded read-only and never resolved.</b> <c>IShellLink::Resolve</c> searches for a
/// target that has moved and can rewrite the shortcut to point at what it found, and §7.5 writes
/// nothing but one <c>StartupApproved</c> value. So a shortcut whose target has gone reads as
/// pointing at a missing file, which is what it does when Windows starts it.</para>
/// </summary>
public sealed class ShellShortcutReader : IShortcutReader
{
    public static ShellShortcutReader Default { get; } = new();

    private const int ReadOnly = 0;
    private const int MaxPath = 32_767;

    private ShellShortcutReader()
    {
    }

    public ShortcutTarget? Read(string path)
    {
        object? link = null;

        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(LongPath.Extended(path), ReadOnly);

            var shell = (IShellLinkW)link;
            var target = new StringBuilder(MaxPath);
            var arguments = new StringBuilder(MaxPath);

            // No flags: the path as Windows would run it, with any environment variables expanded.
            shell.GetPath(target, target.Capacity, 0, 0);
            shell.GetArguments(arguments, arguments.Capacity);

            return new ShortcutTarget(target.Length > 0 ? target.ToString() : null, arguments.ToString());
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            // A file named .lnk that is not one, or one the shell would not open: its target is
            // unknown, which the entry says, rather than a reason to drop it.
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>
    /// <c>IShellLinkW</c>, declared to <c>GetArguments</c>: every slot before it is held so the
    /// vtable lines up, and none after it is called.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, nint findData, uint flags);

        void GetIDList(out nint list);

        void SetIDList(nint list);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
    }
}
