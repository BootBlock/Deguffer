using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>
/// Reads which process hosts each running service, through <c>EnumServicesStatusEx</c> with
/// <c>SC_ENUM_PROCESS_INFO</c>.
///
/// <para>That call leaves out, with no error, the services this account may not query. Nothing here
/// can detect that, which is why <see cref="ServiceListing.Listed"/> says so rather than meaning
/// "every service".</para>
/// </summary>
internal sealed partial class ServiceTableReader
{
    private const uint EnumerateService = 0x0004;
    private const int EnumProcessInfo = 0;
    private const uint Win32Services = 0x30;
    private const uint ActiveServices = 0x1;
    private const int ErrorMoreData = 234;

    /// <summary>The largest buffer the call documents accepting.</summary>
    private const int BufferLength = 64_000;

    /// <summary>
    /// Enough calls for several thousand services. Each call returns at least one, so this only ends a
    /// read that has stopped making progress.
    /// </summary>
    private const int Calls = 64;

    private readonly byte[] _buffer = SystemInformation.PinnedArray<byte>(BufferLength);

    public ServiceTable Read()
    {
        var manager = OpenSCManager(null, null, EnumerateService);

        if (manager == 0)
        {
            return new ServiceTable([], ServiceListing.NotListed);
        }

        try
        {
            return ReadFrom(manager);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private ServiceTable ReadFrom(nint manager)
    {
        var services = new List<RunningService>();
        var resume = 0;
        var address = SystemInformation.AddressOf(_buffer);

        for (var call = 0; call < Calls; call++)
        {
            var finished = EnumServicesStatusEx(
                manager, EnumProcessInfo, Win32Services, ActiveServices,
                address, _buffer.Length, out _, out var returned, ref resume, null);

            var more = !finished && Marshal.GetLastPInvokeError() == ErrorMoreData;

            if ((!finished && !more)
                || !ServiceRecordParser.Parse(_buffer, (ulong)(nuint)address, returned, IntPtr.Size, services))
            {
                return new ServiceTable(services, ServiceListing.ListedInPart);
            }

            if (finished)
            {
                return new ServiceTable(services, ServiceListing.Listed);
            }

            // More to come and nothing returned means a single entry larger than the documented
            // maximum buffer, which the call cannot hand over at all.
            if (returned == 0)
            {
                break;
            }
        }

        return new ServiceTable(services, ServiceListing.ListedInPart);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(
        nint manager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        nint buffer,
        int bufferSize,
        out int bytesNeeded,
        out int servicesReturned,
        ref int resumeHandle,
        string? groupName);
}
