using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Storage;
using Windows.Storage.Provider;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A real sync root, registered for the signed-in user over a scratch folder and connected by this
/// process as its sync app, so the Cloud Files calls <see cref="Cloud.CloudFiles"/> makes can be
/// observed against real placeholders rather than assumed from the documentation.
///
/// <para><b>It connects and answers nothing.</b> No callback is registered, so anything that tried to
/// download a file's data would wait on this process and then fail. A read that returns promptly with
/// an online-only file still online-only is the evidence that nothing was downloaded.</para>
///
/// <para><b>Registered under its own name, and removed again.</b> The identifier starts
/// <c>DegufferTests!</c>, which no real sync app uses, and <see cref="Dispose"/> disconnects before it
/// unregisters, because Windows refuses to unregister a root that is still connected.</para>
/// </summary>
public sealed class ScratchSyncRoot : IDisposable
{
    public const string ProviderName = "DegufferTests";

    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x0007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const int ConvertMarkInSync = 0x0001;
    private const int CreateMarkInSync = 0x0002;
    private const uint CallbackTypeNone = 0xFFFF_FFFF;

    private long _connection;

    public ScratchSyncRoot(string path)
    {
        Path = path;
        Id = $"{ProviderName}!{WindowsIdentity.GetCurrent().User!.Value}!{Guid.NewGuid():N}";

        Directory.CreateDirectory(path);

        StorageProviderSyncRootManager.Register(new StorageProviderSyncRootInfo
        {
            Id = Id,
            Path = StorageFolder.GetFolderFromPathAsync(path).AsTask().GetAwaiter().GetResult(),
            DisplayNameResource = "Deguffer tests",
            IconResource = @"C:\Windows\System32\imageres.dll,-1043",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.Default,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            Version = "1.0",
        });

        Connect();
    }

    public string Path { get; }

    public string Id { get; }

    public string At(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    /// <summary>An ordinary file of <paramref name="bytes"/> bytes, which stays one.</summary>
    public string PlainFile(string name, int bytes)
    {
        var path = At(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// A placeholder with all of its data on this PC and marked in sync: "locally available", the state
    /// a released file starts from.
    /// </summary>
    public string LocalCopy(string name, int bytes)
    {
        var path = PlainFile(name, bytes);
        Convert(path);
        return path;
    }

    /// <summary>A folder converted to a placeholder, so it can carry a pin of its own.</summary>
    public string Folder(string name)
    {
        var path = At(name);
        Directory.CreateDirectory(path);
        Convert(path);
        return path;
    }

    /// <summary>A placeholder with none of its data on this PC: "online-only".</summary>
    public string OnlineOnly(string name, long bytes)
    {
        var identity = Marshal.StringToHGlobalUni(name);
        var relative = Marshal.StringToHGlobalUni(name);

        try
        {
            var now = DateTime.UtcNow.ToFileTimeUtc();
            var info = new[]
            {
                new PlaceholderCreateInfo
                {
                    RelativeFileName = relative,
                    FsMetadata = new FsMetadata
                    {
                        CreationTime = now,
                        LastAccessTime = now,
                        LastWriteTime = now,
                        ChangeTime = now,
                        FileAttributes = 0x80,
                        FileSize = bytes,
                    },
                    FileIdentity = identity,
                    FileIdentityLength = (uint)((name.Length + 1) * sizeof(char)),
                    Flags = CreateMarkInSync,
                },
            };

            Marshal.ThrowExceptionForHR(CfCreatePlaceholders(Path, info, 1, 0, out _));
            Marshal.ThrowExceptionForHR(info[0].Result);
        }
        finally
        {
            Marshal.FreeHGlobal(identity);
            Marshal.FreeHGlobal(relative);
        }

        return At(name);
    }

    /// <summary>Set a pin as the user or the sync app would, on this one entry.</summary>
    public void Pin(string path, Cloud.PinState state)
    {
        using var handle = Open(path, FileReadAttributes);
        Marshal.ThrowExceptionForHR(CfSetPinState(handle, (int)state, 0, 0));
    }

    /// <summary>
    /// Stop being the sync app, as closing it would, and so see its placeholders as every other process
    /// does.
    ///
    /// <para>Windows disguises a placeholder as an ordinary file to every process but the one connected
    /// to its root, and a connected process sees it undisguised whatever its thread asks for, as a probe
    /// showed. Deguffer is never the sync app, so a test of what Deguffer's own calls see disconnects
    /// first. The placeholders keep every state they had.</para>
    /// </summary>
    public void Disconnect()
    {
        if (_connection != 0)
        {
            Marshal.ThrowExceptionForHR(CfDisconnectSyncRoot(_connection));
            _connection = 0;
        }
    }

    public void Dispose()
    {
        Disconnect();
        StorageProviderSyncRootManager.Unregister(Id);
    }

    private void Connect()
    {
        var callbacks = new[] { new CallbackRegistration { Type = CallbackTypeNone } };
        Marshal.ThrowExceptionForHR(CfConnectSyncRoot(Path, callbacks, 0, 0, out _connection));
    }

    private static void Convert(string path)
    {
        using var handle = Open(path, GenericRead | GenericWrite);
        var identity = System.Text.Encoding.UTF8.GetBytes(System.IO.Path.GetFileName(path));
        Marshal.ThrowExceptionForHR(CfConvertToPlaceholder(handle, identity, (uint)identity.Length, ConvertMarkInSync, 0, 0));
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, ShareAll, 0, OpenExisting, BackupSemantics, 0);

        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open {path}.", Marshal.GetHRForLastWin32Error());
        }

        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackRegistration
    {
        public uint Type;
        public nint Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FsMetadata
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlaceholderCreateInfo
    {
        public nint RelativeFileName;
        public FsMetadata FsMetadata;
        public nint FileIdentity;
        public uint FileIdentityLength;
        public int Flags;
        public int Result;
        public long CreateUsn;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, uint share, nint securityAttributes, uint disposition, uint flags, nint template);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfConnectSyncRoot(
        string syncRootPath, CallbackRegistration[] callbackTable, nint callbackContext, int connectFlags, out long connectionKey);

    [DllImport("cldapi.dll")]
    private static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport("cldapi.dll")]
    private static extern int CfConvertToPlaceholder(
        SafeFileHandle fileHandle, byte[] fileIdentity, uint fileIdentityLength, int convertFlags, nint convertUsn, nint overlapped);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfCreatePlaceholders(
        string baseDirectoryPath, [In, Out] PlaceholderCreateInfo[] placeholderArray, uint placeholderCount, int createFlags, out uint entriesProcessed);

    [DllImport("cldapi.dll")]
    private static extern int CfSetPinState(SafeFileHandle fileHandle, int pinState, int pinFlags, nint overlapped);
}
