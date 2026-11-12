using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Deguffer.Core.Safety;
using Deguffer.Core.Wmi;

namespace Deguffer.Core.Providers;

/// <summary>
/// The machine's <see cref="IDeliveryOptimizationStatus"/>: <c>CacheSizeBytes</c> of Delivery
/// Optimization's <c>MSFT_DOCurrentStatus</c> class in WMI's
/// <c>ROOT\Microsoft\Windows\DeliveryOptimization</c> namespace, where the module's
/// <c>Get-DeliveryOptimizationPerfSnap</c> takes the same figure from.
///
/// <para><b>In-process, through WMI's own COM interfaces</b> (<see cref="WbemConnection"/>), rather than
/// through the cmdlet. Starting Windows PowerShell for it cost one to two seconds of every preview and
/// two more starts for every clean, loaded a module that also lists every file the service holds, and
/// is a route a policy can switch off. The class answered an unelevated process on Windows 11 24H2,
/// where the cache's own folder refuses a listing.</para>
/// </summary>
public sealed class WindowsDeliveryOptimizationStatus : IDeliveryOptimizationStatus
{
    /// <summary>The one instance the app runs with (G5). It holds nothing between calls.</summary>
    public static readonly WindowsDeliveryOptimizationStatus Machine = new();

    private const string StatusNamespace = @"ROOT\Microsoft\Windows\DeliveryOptimization";

    private const string StatusQuery = "SELECT CacheSizeBytes FROM MSFT_DOCurrentStatus";

    /// <summary>
    /// How long, in milliseconds, each wait for the service's answer lasts before the token is looked
    /// at again, so a cancelled scan stops waiting within this rather than when WMI gives up.
    /// </summary>
    private const int CancellationPoll = 100;

    private WindowsDeliveryOptimizationStatus()
    {
    }

    public DeliveryOptimizationReading Read(CancellationToken ct)
    {
        if (WbemConnection.Connect(StatusNamespace, out var hr) is not { } services)
        {
            return Unanswered(hr);
        }

        try
        {
            hr = services.ExecQuery("WQL", StatusQuery, WbemConnection.SemiSynchronous, null, out var instances);

            if (hr != WbemConnection.S_OK)
            {
                return Unanswered(hr);
            }

            try
            {
                return Read(instances, ct);
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
    /// The one status the service reports. None is no figure, and more than one is a figure Deguffer
    /// could only pick from, so both are unanswered rather than read as an empty cache. The wait is
    /// held to <see cref="ToolRun.QueryLimit"/>, the limit the PowerShell query had.
    /// </summary>
    private static DeliveryOptimizationReading Read(IEnumWbemClassObject instances, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        DeliveryOptimizationReading? reading = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var hr = instances.Next(CancellationPoll, 1, out var instance, out var returned);

            if (hr < 0)
            {
                return Unanswered(hr);
            }

            if (returned == 0)
            {
                if (hr != WbemConnection.TimedOut)
                {
                    return reading ?? DeliveryOptimizationReading.Unanswered("Windows gave no figure.");
                }

                if (waited.Elapsed >= ToolRun.QueryLimit)
                {
                    return DeliveryOptimizationReading.Unanswered(
                        $"Delivery Optimization did not answer within {ToolRun.QueryLimit.TotalMinutes:0} minutes.");
                }

                continue;
            }

            try
            {
                if (reading is not null)
                {
                    return DeliveryOptimizationReading.Unanswered(
                        "Windows reported more than one Delivery Optimization status, so the figure is not known.");
                }

                reading = instance.Get("CacheSizeBytes", 0, out var value, out _, out _) == WbemConnection.S_OK
                    ? ReadingOf(value)
                    : DeliveryOptimizationReading.Unanswered("Windows gave no figure.");
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    /// <summary>
    /// <c>CacheSizeBytes</c> as WMI hands it over. A <c>uint64</c> travels as a string of invariant
    /// digits, because WMI's VARIANTs predate a 64-bit integer, and a property the service left unset
    /// arrives as a null VARIANT. Anything but one non-negative whole number that fits a size is not an
    /// answer.
    /// </summary>
    internal static DeliveryOptimizationReading ReadingOf(object? value) => value switch
    {
        string text when ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            && bytes <= long.MaxValue => DeliveryOptimizationReading.Of((long)bytes),
        null or DBNull => DeliveryOptimizationReading.Unanswered("Windows gave no figure."),
        _ => DeliveryOptimizationReading.Unanswered(
            string.Create(CultureInfo.InvariantCulture, $"Windows answered '{value}', which is not a size.")),
    };

    private static DeliveryOptimizationReading Unanswered(int hr) =>
        DeliveryOptimizationReading.Unanswered(
            WbemConnection.IsAccessDenied(hr)
                ? "Windows refused this account the figure."
                : $"Windows did not answer (error 0x{hr:X8}).");
}
