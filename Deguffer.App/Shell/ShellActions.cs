using System.Diagnostics;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;

namespace Deguffer.App.Shell;

/// <summary>
/// The three things Explore does with an item that are not a deletion: open it, show it where it
/// lives, and put the Windows properties sheet on it.
///
/// <para>None of them changes anything on disk, which is why none of them is behind
/// <see cref="Deguffer.Core.Exploring.Acting.ExploreActionPolicy"/>. §7.1 constrains what Explore
/// <em>acts on</em>, and the acting it constrains is the removal — refusing to open a folder that
/// Explorer will open anyway would be theatre rather than safety.</para>
///
/// <para>In the app rather than in Core because each one hands a path to another program's user
/// interface, and Core has no business starting one. Each returns what went wrong rather than
/// throwing: a file whose association has been removed, and a path that vanished between the scan
/// and the click, are both ordinary on a machine Explore is pointed at.</para>
/// </summary>
public static class ShellActions
{
    private const int SeeMaskInvokeIdList = 0x0000000C;
    private const int SeeMaskFlagNoUi = 0x00000400;

    /// <summary>
    /// Open the item with whatever handles it: the default program for a file, an Explorer window
    /// for a folder. Null on success, or a sentence for the user.
    /// </summary>
    public static string? Open(string path) => Start(new ProcessStartInfo(Display(path))
    {
        // The shell's own resolution, which is the whole point: without it this would try to
        // execute the file rather than open it with the program the user has chosen.
        UseShellExecute = true,
    });

    /// <summary>
    /// Show the item where it lives: Explorer at the parent with a file selected, and at the folder
    /// itself for a folder. Null on success, or a sentence for the user.
    /// </summary>
    public static string? Reveal(string path, bool isDirectory)
    {
        var display = Display(path);

        // Quoted, because a path with a space in it is the ordinary case and Explorer parses this
        // argument itself. The comma after /select is Explorer's own syntax and is not a typo.
        var arguments = isDirectory ? $"\"{display}\"" : $"/select,\"{display}\"";

        return Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    /// <summary>
    /// The standard Windows properties sheet. Null on success, or a sentence for the user.
    ///
    /// <para>Through <c>ShellExecuteEx</c> rather than <see cref="Process"/>, because the verb needs
    /// the item's shell identifier list to work at all — <c>SEE_MASK_INVOKEIDLIST</c> is what makes
    /// "properties" resolve, and .NET's own process start does not set it.</para>
    /// </summary>
    public static string? Properties(string path)
    {
        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskInvokeIdList | SeeMaskFlagNoUi,
            Verb = "properties",
            File = Display(path),
            Show = 1,
        };

        return ShellExecuteEx(ref info)
            ? null
            : $"Windows would not show the properties for this (0x{Marshal.GetLastWin32Error():X8}).";
    }

    /// <summary>
    /// The display form, always. Every one of these hands the path to another program's command
    /// line or to the shell namespace, and neither accepts the extended-length prefix §6.3 requires
    /// of a filesystem call — <see cref="Deguffer.Core.Execution.IRecycleBin"/> says the same of the
    /// Recycle Bin. Normalised on the way through for the same reason it is there: a value carrying
    /// a <c>..</c> segment would open a directory nobody pointed at.
    /// </summary>
    private static string Display(string path) => LongPath.Display(LongPath.Extended(path));

    private static string? Start(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // No association, no permission, or the item has gone since the scan. All ordinary, and
            // Windows' own message is more specific than anything this could write.
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // A path the shell would not accept as a target at all.
            return ex.Message;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public int Mask;
        public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show;
        public IntPtr InstanceApp;
        public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr KeyClass;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Process;
    }
}
