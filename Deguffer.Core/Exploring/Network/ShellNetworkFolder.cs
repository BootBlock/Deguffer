using System.Runtime.InteropServices;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// <see cref="INetworkNeighbourhood"/> on this machine: the items of the shell's Network folder,
/// kept where the shell reaches them as <c>\\name</c>.
///
/// <para>Every call answers its <c>HRESULT</c> as a value, so a Network folder that refuses, as it
/// does with discovery off, is a <see cref="NetworkFailure"/> on its node rather than an exception.</para>
/// </summary>
public sealed class ShellNetworkFolder : INetworkNeighbourhood
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static ShellNetworkFolder Default { get; } = new();

    /// <summary><c>FOLDERID_NetworkFolder</c>.</summary>
    private static readonly Guid NetworkFolder = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");

    /// <summary><c>BHID_EnumItems</c>: the handler that lists a folder's items as shell items.</summary>
    private static readonly Guid EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");

    /// <summary><c>SIGDN_DESKTOPABSOLUTEPARSING</c>: <c>\\NAS</c> for a computer, and a <c>::{GUID}</c> path for a device.</summary>
    private const uint ParsingName = 0x8002_8000;

    private ShellNetworkFolder()
    {
    }

    public NetworkListing<string> Computers(CancellationToken ct)
    {
        var folderId = NetworkFolder;
        var itemId = typeof(IShellItem).GUID;

        if (SHGetKnownFolderItem(ref folderId, 0, 0, ref itemId, out var folder) < 0)
        {
            return NetworkListing<string>.Failed(NetworkFailure.Refused);
        }

        try
        {
            var handler = EnumItems;
            var enumId = typeof(IEnumShellItems).GUID;

            if (folder.BindToHandler(0, ref handler, ref enumId, out var listing) < 0)
            {
                return NetworkListing<string>.Failed(NetworkFailure.Refused);
            }

            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(listing);
            Marshal.Release(listing);

            try
            {
                return NetworkListing<string>.Listed(Names(items, ct));
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }
    }

    private static List<string> Names(IEnumShellItems items, CancellationToken ct)
    {
        var names = new List<string>();

        while (!ct.IsCancellationRequested && items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                if (item.GetDisplayName(ParsingName, out var name) == 0)
                {
                    var parsed = Marshal.PtrToStringUni(name);
                    Marshal.FreeCoTaskMem(name);

                    if (ComputerOf(parsed) is { } computer)
                    {
                        names.Add(computer);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        ct.ThrowIfCancellationRequested();

        return names;
    }

    /// <summary>The computer <paramref name="parsed"/> names as <c>\\name</c>, or null for anything else the folder holds.</summary>
    private static string? ComputerOf(string? parsed) =>
        parsed is { Length: > 2 } && parsed.StartsWith(@"\\", StringComparison.Ordinal) && parsed.IndexOf('\\', 2) < 0
            ? parsed[2..]
            : null;

    // DllImport rather than LibraryImport: the generator does not marshal a ComImport interface.
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderItem(
        ref Guid folderId, uint flags, nint token, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}

/// <summary>The shell items of a folder, one at a time. <c>Next</c> is called; the rest hold their slots.</summary>
[ComImport]
[Guid("70629033-E363-4A28-A567-0DB78006E6D7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumShellItems
{
    [PreserveSig]
    int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out IEnumShellItems copy);
}
