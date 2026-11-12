using System.Runtime.InteropServices;

namespace Deguffer.Core.Wmi;

/// <summary>
/// A connection to one WMI namespace on this machine, through WMI's own COM interfaces rather than
/// <c>System.Management</c>, which would be Core's first package for a handful of calls.
///
/// <para>Shared by every caller of WMI, so each sets the proxy's security the same way: a connection
/// made without it runs the provider as nobody, and a query then answers with nothing.</para>
/// </summary>
internal static class WbemConnection
{
    public const int S_OK = 0;

    /// <summary><c>WBEM_FLAG_RETURN_IMMEDIATELY | WBEM_FLAG_FORWARD_ONLY</c>.</summary>
    public const int SemiSynchronous = 0x10 | 0x20;

    /// <summary><c>WBEM_INFINITE</c>.</summary>
    public const int Infinite = -1;

    /// <summary>
    /// <c>WBEM_S_TIMEDOUT</c>: <c>IEnumWbemClassObject.Next</c> waited its timeout with nothing to give.
    /// A success code, so a caller that waits a finite time tells it apart from the end of the results.
    /// </summary>
    public const int TimedOut = 0x40004;

    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary><c>WBEM_E_ACCESS_DENIED</c>.</summary>
    private const int WbemAccessDenied = unchecked((int)0x80041003);

    private const uint InProcessServer = 0x1;

    private static readonly Guid WbemLocator = new("4590f811-1d3a-11d0-891f-00aa004b2e24");

    private static readonly Guid WbemLocatorInterface = new("dc12a687-737f-11cf-884d-00aa004b2e24");

    /// <summary>Whether <paramref name="hr"/> is a refusal for want of rights, in either the COM or the WMI form.</summary>
    public static bool IsAccessDenied(int hr) => hr is AccessDenied or WbemAccessDenied;

    /// <summary>
    /// The namespace <paramref name="path"/> on this machine, with its proxy set to impersonate the
    /// caller, or null with the failure in <paramref name="hr"/>. The caller releases what it is given.
    /// </summary>
    public static IWbemServices? Connect(string path, out int hr)
    {
        var classId = WbemLocator;
        var interfaceId = WbemLocatorInterface;
        hr = CoCreateInstance(ref classId, IntPtr.Zero, InProcessServer, ref interfaceId, out var created);

        if (hr != S_OK)
        {
            return null;
        }

        var locator = (IWbemLocator)created;

        try
        {
            hr = locator.ConnectServer(path, null, null, null, 0, null, null, out var services);

            if (hr != S_OK)
            {
                return null;
            }

            hr = Impersonating(services);

            if (hr != S_OK)
            {
                Marshal.ReleaseComObject(services);
                return null;
            }

            return services;
        }
        finally
        {
            Marshal.ReleaseComObject(locator);
        }
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
