using System.Globalization;
using System.Runtime.InteropServices;

namespace Deguffer.Core.SystemProtection;

/// <summary>
/// System Restore's own interfaces: its <c>SystemRestore</c> class in WMI's <c>root\default</c>
/// namespace, which is how Microsoft documents listing restore points, and
/// <c>SRRemoveRestorePoint</c>, which is how it documents removing one.
///
/// <para><b>WMI's own COM interfaces rather than <c>System.Management</c></b>, which would be Core's
/// first package for one query, and rather than <c>Get-ComputerRestorePoint</c>, which is the same
/// query behind a PowerShell a policy can switch off. Slot order is <c>WbemCli.h</c>'s, for the reason
/// <see cref="Exploring.Hidden.ShadowStorageCalls"/> gives.</para>
///
/// <para><b>Both refuse an unelevated process.</b> The listing answered "Access denied" to one on
/// Windows 11, and Microsoft's cmdlet over the same class says to run it as administrator.</para>
/// </summary>
internal static class RestorePointCalls
{
    private const int S_OK = 0;

    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>WBEM_E_ACCESS_DENIED</c>.</summary>
    private const int WbemAccessDenied = unchecked((int)0x80041003);

    private const uint InProcessServer = 0x1;

    /// <summary><c>WBEM_FLAG_RETURN_IMMEDIATELY | WBEM_FLAG_FORWARD_ONLY</c>.</summary>
    private const int SemiSynchronous = 0x10 | 0x20;

    /// <summary><c>WBEM_INFINITE</c>.</summary>
    private const int Infinite = -1;

    private const uint ErrorSuccess = 0;

    private const uint ErrorAccessDenied = 5;

    /// <summary><c>ERROR_INVALID_DATA</c>: "does not exist or cannot be removed".</summary>
    private const uint ErrorInvalidData = 13;

    private static readonly Guid WbemLocator = new("4590f811-1d3a-11d0-891f-00aa004b2e24");

    private static readonly Guid WbemLocatorInterface = new("dc12a687-737f-11cf-884d-00aa004b2e24");

    public static RestorePointListing List()
    {
        var classId = WbemLocator;
        var interfaceId = WbemLocatorInterface;
        var hr = CoCreateInstance(ref classId, IntPtr.Zero, InProcessServer, ref interfaceId, out var created);

        if (hr != S_OK)
        {
            return Unanswered(hr);
        }

        var locator = (IWbemLocator)created;

        try
        {
            hr = locator.ConnectServer(@"ROOT\DEFAULT", null, null, null, 0, null, null, out var services);

            if (hr != S_OK)
            {
                return Unanswered(hr);
            }

            try
            {
                hr = Impersonating(services);

                if (hr != S_OK)
                {
                    return Unanswered(hr);
                }

                hr = services.ExecQuery(
                    "WQL",
                    "SELECT SequenceNumber, CreationTime, Description FROM SystemRestore",
                    SemiSynchronous,
                    null,
                    out var instances);

                if (hr != S_OK)
                {
                    return Unanswered(hr);
                }

                try
                {
                    return Read(instances);
                }
                finally
                {
                    Marshal.ReleaseComObject(instances);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(services);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(locator);
        }
    }

    /// <summary>
    /// <c>SRRemoveRestorePoint</c>, loaded by name from Windows' own copy of its library, as Microsoft
    /// says it must be rather than linked: a missing export is then an answer rather than a crash.
    /// </summary>
    public static unsafe RemovalAnswer Remove(string library, uint sequenceNumber)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return RemovalAnswer.Failed;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "SRRemoveRestorePoint", out var export))
            {
                return RemovalAnswer.Failed;
            }

            var result = ((delegate* unmanaged[Stdcall]<uint, uint>)export)(sequenceNumber);

            return result switch
            {
                ErrorSuccess => RemovalAnswer.Removed,
                ErrorInvalidData => RemovalAnswer.NotRemovable,
                ErrorAccessDenied => RemovalAnswer.NeedsElevation,
                _ => RemovalAnswer.Failed,
            };
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// Every instance, read one at a time. An instance that stops answering part way, or lacks a field,
    /// fails the whole listing: a restore point missing from the list is one the plan would not know to
    /// keep, and the newest is decided from the list.
    /// </summary>
    private static RestorePointListing Read(IEnumWbemClassObject instances)
    {
        List<RestorePoint> points = [];

        while (true)
        {
            var hr = instances.Next(Infinite, 1, out var instance, out var returned);

            if (hr < 0)
            {
                return Unanswered(hr);
            }

            if (returned == 0)
            {
                return RestorePointListing.Of(points);
            }

            try
            {
                if (Point(instance) is not { } point)
                {
                    return RestorePointListing.Failed("System Restore listed a restore point Deguffer could not read.");
                }

                points.Add(point);
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    private static RestorePoint? Point(IWbemClassObject instance)
    {
        if (instance.Get("SequenceNumber", 0, out var number, out _, out _) != S_OK
            || instance.Get("CreationTime", 0, out var created, out _, out _) != S_OK
            || instance.Get("Description", 0, out var description, out _, out _) != S_OK)
        {
            return null;
        }

        // WMI hands a uint32 over as a signed four-byte VARIANT.
        uint? sequence = number switch
        {
            int signed => unchecked((uint)signed),
            uint unsigned => unsigned,
            _ => null,
        };

        return sequence is { } n && created is string time && CimTime(time) is { } when
            ? new RestorePoint(n, when, description as string ?? string.Empty)
            : null;
    }

    /// <summary>
    /// A <c>CIM_DATETIME</c>, <c>yyyymmddHHMMSS.mmmmmmsUUU</c>: the time, then the offset from UTC in
    /// minutes. Null where it does not read as one.
    /// </summary>
    /// <summary>Fourteen hours, the furthest from UTC an offset can be.</summary>
    private const int MaximumOffset = 14 * 60;

    internal static DateTime? CimTime(string text)
    {
        if (text.Length != 25
            || !DateTime.TryParseExact(text[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(text[22..], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || text[21] is not ('+' or '-')
            || minutes > MaximumOffset)
        {
            return null;
        }

        var offset = TimeSpan.FromMinutes(text[21] == '-' ? -minutes : minutes);

        return new DateTimeOffset(local, offset).LocalDateTime;
    }

    /// <summary>
    /// Every Microsoft example sets the proxy's security before the first call through it, because
    /// WMI's default does not impersonate the caller, and a query the provider runs as nobody returns
    /// nothing.
    /// </summary>
    private static int Impersonating(IWbemServices services)
    {
        var proxy = Marshal.GetComInterfaceForObject<IWbemServices, IWbemServices>(services);

        try
        {
            return CoSetProxyBlanket(proxy, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.Release(proxy);
        }
    }

    private static RestorePointListing Unanswered(int hr) =>
        hr is AccessDenied or WbemAccessDenied
            ? RestorePointListing.Refused
            : RestorePointListing.Failed($"System Restore did not list its restore points (error 0x{hr:X8}).");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    /// <param name="authentication"><c>RPC_C_AUTHN_WINNT</c>.</param>
    /// <param name="authorisation"><c>RPC_C_AUTHZ_NONE</c>.</param>
    /// <param name="level"><c>RPC_C_AUTHN_LEVEL_CALL</c>.</param>
    /// <param name="impersonation"><c>RPC_C_IMP_LEVEL_IMPERSONATE</c>.</param>
    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket(
        IntPtr proxy,
        uint authentication,
        uint authorisation,
        IntPtr serverName,
        uint level,
        uint impersonation,
        IntPtr identity,
        uint capabilities);
}

[ComImport]
[Guid("dc12a687-737f-11cf-884d-00aa004b2e24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemLocator
{
    [PreserveSig]
    int ConnectServer(
        [MarshalAs(UnmanagedType.BStr)] string networkResource,
        [MarshalAs(UnmanagedType.BStr)] string? user,
        [MarshalAs(UnmanagedType.BStr)] string? password,
        [MarshalAs(UnmanagedType.BStr)] string? locale,
        int securityFlags,
        [MarshalAs(UnmanagedType.BStr)] string? authority,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        out IWbemServices services);
}

/// <summary>Declared as far as <c>ExecQuery</c>, the eighteenth slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("9556dc99-828c-11cf-a37e-00aa003240c7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemServices
{
    [PreserveSig] int OpenNamespace();

    [PreserveSig] int CancelAsyncCall();

    [PreserveSig] int QueryObjectSink();

    [PreserveSig] int GetObject();

    [PreserveSig] int GetObjectAsync();

    [PreserveSig] int PutClass();

    [PreserveSig] int PutClassAsync();

    [PreserveSig] int DeleteClass();

    [PreserveSig] int DeleteClassAsync();

    [PreserveSig] int CreateClassEnum();

    [PreserveSig] int CreateClassEnumAsync();

    [PreserveSig] int PutInstance();

    [PreserveSig] int PutInstanceAsync();

    [PreserveSig] int DeleteInstance();

    [PreserveSig] int DeleteInstanceAsync();

    [PreserveSig] int CreateInstanceEnum();

    [PreserveSig] int CreateInstanceEnumAsync();

    [PreserveSig]
    int ExecQuery(
        [MarshalAs(UnmanagedType.BStr)] string language,
        [MarshalAs(UnmanagedType.BStr)] string query,
        int flags,
        [MarshalAs(UnmanagedType.IUnknown)] object? context,
        out IEnumWbemClassObject instances);
}

/// <summary>Declared as far as <c>Next</c>, the second slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("027947e1-d731-11ce-a357-000000000001")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumWbemClassObject
{
    [PreserveSig] int Reset();

    /// <summary>Asked for one object at a time, so the array it fills is a single pointer.</summary>
    [PreserveSig]
    int Next(int timeout, uint count, out IWbemClassObject instance, out uint returned);
}

/// <summary>Declared as far as <c>Get</c>, the second slot after <c>IUnknown</c>'s.</summary>
[ComImport]
[Guid("dc12a681-737f-11cf-884d-00aa004b2e24")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWbemClassObject
{
    [PreserveSig] int GetQualifierSet();

    [PreserveSig]
    int Get(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        int flags,
        out object? value,
        out int type,
        out int flavour);
}
