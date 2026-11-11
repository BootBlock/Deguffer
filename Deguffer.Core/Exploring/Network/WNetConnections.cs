using System.Runtime.InteropServices;

namespace Deguffer.Core.Exploring.Network;

/// <summary>
/// <see cref="INetworkConnections"/> on this machine, through the network provider calls in
/// <c>mpr.dll</c>. Each call answers rather than throws, so a provider that refuses is a
/// <see cref="NetworkFailure"/> the tree states, and never an exception a handler drops.
/// </summary>
public sealed partial class WNetConnections : INetworkConnections
{
    /// <summary>The one instance the app runs with (G5).</summary>
    public static WNetConnections Default { get; } = new();

    private const uint ResourceConnected = 1;
    private const uint ResourceRemembered = 3;
    private const uint ResourceTypeAny = 0;
    private const uint ResourceTypeDisk = 1;

    /// <summary><c>CONNECT_INTERACTIVE | CONNECT_PROMPT</c>: always ask through the credentials prompt, never with what Windows has saved alone.</summary>
    private const uint ConnectThroughPrompt = 0x08 | 0x10;

    /// <summary>What one enumeration call is handed, which holds a few dozen connections. Grown where one entry does not fit.</summary>
    private const int FirstBuffer = 16 * 1024;

    private WNetConnections()
    {
    }

    public NetworkListing<NetworkConnection> Connected() => Enumerate(ResourceConnected);

    public NetworkListing<NetworkConnection> Remembered() => Enumerate(ResourceRemembered);

    public unsafe NetworkSignIn SignIn(string remote, nint owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(remote);

        int code;

        fixed (char* name = remote)
        {
            var resource = new NetResource { Type = ResourceTypeAny, RemoteName = name };

            code = WNetAddConnection3(owner, &resource, null, null, ConnectThroughPrompt);
        }

        return code switch
        {
            NetworkErrors.Success => NetworkSignIn.Connected,
            NetworkErrors.Cancelled => NetworkSignIn.Cancelled,
            _ => NetworkSignIn.Failed(NetworkErrors.Of(code)),
        };
    }

    private static unsafe NetworkListing<NetworkConnection> Enumerate(uint scope)
    {
        var opened = WNetOpenEnum(scope, ResourceTypeDisk, 0, null, out var handle);

        if (opened == NetworkErrors.NoNetwork)
        {
            // No network provider is running, which is a machine with no connections, not a refusal.
            return NetworkListing<NetworkConnection>.Empty;
        }

        if (opened != NetworkErrors.Success)
        {
            return NetworkListing<NetworkConnection>.Failed(NetworkErrors.Of(opened));
        }

        try
        {
            var connections = new List<NetworkConnection>();
            var buffer = new byte[FirstBuffer];

            while (true)
            {
                var count = -1;
                var size = buffer.Length;
                int code;

                fixed (byte* start = buffer)
                {
                    code = WNetEnumResource(handle, ref count, start, ref size);

                    if (code == NetworkErrors.Success)
                    {
                        var entries = (NetResource*)start;

                        for (var i = 0; i < count; i++)
                        {
                            if (Read(entries[i]) is { } connection)
                            {
                                connections.Add(connection);
                            }
                        }

                        continue;
                    }
                }

                switch (code)
                {
                    case NetworkErrors.NoMoreItems:
                        return NetworkListing<NetworkConnection>.Listed(connections);

                    case NetworkErrors.MoreData:
                        buffer = new byte[size];
                        break;

                    default:
                        return NetworkListing<NetworkConnection>.Failed(NetworkErrors.Of(code));
                }
            }
        }
        finally
        {
            _ = WNetCloseEnum(handle);
        }
    }

    private static unsafe NetworkConnection? Read(in NetResource resource)
    {
        var remote = resource.RemoteName is null ? null : new string(resource.RemoteName);

        if (string.IsNullOrEmpty(remote))
        {
            return null;
        }

        var local = resource.LocalName is null ? null : new string(resource.LocalName);

        return new NetworkConnection(remote, string.IsNullOrEmpty(local) ? null : local);
    }

    /// <summary><c>NETRESOURCEW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public char* LocalName;
        public char* RemoteName;
        public char* Comment;
        public char* Provider;
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetOpenEnumW")]
    private static unsafe partial int WNetOpenEnum(uint scope, uint type, uint usage, NetResource* resource, out nint handle);

    [LibraryImport("mpr.dll", EntryPoint = "WNetEnumResourceW")]
    private static unsafe partial int WNetEnumResource(nint handle, ref int count, byte* buffer, ref int size);

    [LibraryImport("mpr.dll")]
    private static partial int WNetCloseEnum(nint handle);

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection3W", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int WNetAddConnection3(nint owner, NetResource* resource, string? password, string? user, uint flags);
}
