using System.Globalization;
using System.Runtime.InteropServices;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.SystemProtection;

/// <summary>
/// System Restore's own interfaces: its <c>SystemRestore</c> class in WMI's <c>root\default</c>
/// namespace, which is how Microsoft documents listing restore points, and
/// <c>SRRemoveRestorePoint</c>, which is how it documents removing one.
///
/// <para><b>WMI's own COM interfaces rather than <c>System.Management</c></b>, which would be Core's
/// first package for one query, and rather than <c>Get-ComputerRestorePoint</c>, which is the same
/// query behind a PowerShell a policy can switch off. The interfaces and the connection are
/// <see cref="WbemConnection"/>'s.</para>
///
/// <para><b>Both refuse an unelevated process.</b> The listing answered "Access denied" to one on
/// Windows 11, and Microsoft's cmdlet over the same class says to run it as administrator.</para>
/// </summary>
internal static class RestorePointCalls
{
    private const uint ErrorSuccess = 0;

    private const uint ErrorAccessDenied = 5;

    /// <summary><c>ERROR_INVALID_DATA</c>: "does not exist or cannot be removed".</summary>
    private const uint ErrorInvalidData = 13;

    public static RestorePointListing List()
    {
        if (WbemConnection.Connect(@"ROOT\DEFAULT", out var hr) is not { } services)
        {
            return Unanswered(hr);
        }

        try
        {
            hr = services.ExecQuery(
                "WQL",
                "SELECT SequenceNumber, CreationTime, Description FROM SystemRestore",
                WbemConnection.SemiSynchronous,
                null,
                out var instances);

            if (hr != WbemConnection.S_OK)
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
            var hr = instances.Next(WbemConnection.Infinite, 1, out var instance, out var returned);

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
        if (instance.Get("SequenceNumber", 0, out var number, out _, out _) != WbemConnection.S_OK
            || instance.Get("CreationTime", 0, out var created, out _, out _) != WbemConnection.S_OK
            || instance.Get("Description", 0, out var description, out _, out _) != WbemConnection.S_OK)
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

    private static RestorePointListing Unanswered(int hr) =>
        WbemConnection.IsAccessDenied(hr)
            ? RestorePointListing.Refused
            : RestorePointListing.Failed($"System Restore did not list its restore points (error 0x{hr:X8}).");
}
