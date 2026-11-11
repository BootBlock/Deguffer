using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// <see cref="IShareLister"/> on this machine, through <c>NetShareEnum</c> at level 1: each share's
/// name and type, which is all level 1 reads and all this needs. It needs no administrator rights.
/// </summary>
public sealed partial class NetShareLister : IShareLister
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static NetShareLister Default { get; } = new();

    /// <summary><c>MAX_PREFERRED_LENGTH</c>: one buffer for every share, so a listing is one call.</summary>
    private const int AllAtOnce = -1;

    private const uint TypeMask = 0xFF;
    private const uint Special = 0x8000_0000;

    private NetShareLister()
    {
    }

    public unsafe NetworkListing<ListedShare> Shares(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);

        var shares = new List<ListedShare>();
        var resume = 0;

        while (true)
        {
            var code = NetShareEnum(NetworkNames.Computer(server), 1, out var buffer, AllAtOnce, out var read, out _, ref resume);

            try
            {
                if (code is not (NetworkErrors.Success or NetworkErrors.MoreData))
                {
                    return NetworkListing<ListedShare>.Failed(NetworkErrors.Of(code));
                }

                var entries = (ShareInfo1*)buffer;

                for (var i = 0; i < read; i++)
                {
                    if (entries[i].Name is not null)
                    {
                        shares.Add(new ListedShare(new string(entries[i].Name), KindOf(entries[i].Type), (entries[i].Type & Special) != 0));
                    }
                }
            }
            finally
            {
                if (buffer != 0)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }

            if (code == NetworkErrors.Success)
            {
                return NetworkListing<ListedShare>.Listed(shares);
            }
        }
    }

    private static ShareKind KindOf(uint type) =>
        (type & TypeMask) switch
        {
            0 => ShareKind.Disk,
            1 => ShareKind.PrintQueue,
            3 => ShareKind.Ipc,
            _ => ShareKind.Device,
        };

    /// <summary><c>SHARE_INFO_1</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ShareInfo1
    {
        public char* Name;
        public uint Type;
        public char* Remark;
    }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(
        string server, int level, out nint buffer, int preferredLength, out int read, out int total, ref int resume);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(nint buffer);
}
