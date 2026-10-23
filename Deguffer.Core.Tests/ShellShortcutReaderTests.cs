using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Deguffer.Core.Startup;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real reader against shortcuts the shell itself writes into a scratch folder, so the format is
/// the shell's and not a test's idea of it.
/// </summary>
public sealed class ShellShortcutReaderTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void AShortcutReadsAsItsTargetAndArguments()
    {
        var target = _root.CreateFile(1, "Tool", "tool.exe");
        var shortcut = Shortcut("Tool.lnk", target, "--quiet --tray");

        var read = ShellShortcutReader.Default.Read(shortcut);

        Assert.Equal(new ShortcutTarget(target, "--quiet --tray"), read);
    }

    /// <summary>
    /// Read without resolving, so a target that has gone stays the target the shortcut names and the
    /// shortcut is not rewritten to point at something else (§7.5 writes one value and nothing more).
    /// </summary>
    [Fact]
    public void AShortcutWhoseTargetHasGoneStillNamesItAndIsNotRewritten()
    {
        var target = _root.CreateFile(1, "Gone", "gone.exe");
        var shortcut = Shortcut("Gone.lnk", target, string.Empty);
        File.Delete(target);
        var bytes = File.ReadAllBytes(shortcut);

        var read = ShellShortcutReader.Default.Read(shortcut);

        Assert.Equal(target, read?.Target);
        Assert.Equal(bytes, File.ReadAllBytes(shortcut));
    }

    [Fact]
    public void AFileNamedAsAShortcutThatIsNotOneReadsAsUnknown()
    {
        var fake = _root.CreateFile(16, "Not.lnk");

        Assert.Null(ShellShortcutReader.Default.Read(fake));
    }

    private string Shortcut(string name, string target, string arguments)
    {
        var path = Path.Combine(_root.Path, name);
        WriteShortcut(path, target, arguments);

        return path;
    }

    private static void WriteShortcut(string path, string target, string arguments)
    {
        var link = (IShellLinkW)new ShellLink();

        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary><c>IShellLinkW</c> to <c>SetPath</c>, every slot before it held so the vtable lines up.</summary>
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

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(nint window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
