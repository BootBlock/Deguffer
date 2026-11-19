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

    private WindowsDeliveryOptimizationStatus()
    {
    }

    /// <summary>
    /// The one status the service reports. None is no figure, and more than one is a figure Deguffer
    /// could only pick from, so both are unanswered rather than read as an empty cache. The wait is
    /// held to <see cref="ToolRun.QueryLimit"/>, the limit the PowerShell query had.
    /// </summary>
    public DeliveryOptimizationReading Read(CancellationToken ct)
    {
        if (WbemConnection.Connect(StatusNamespace, out var hr) is not { } services)
        {
            return Unanswered(hr);
        }

        try
        {
            return WbemQuery.Rows(services, StatusQuery, status => ReadingOf(WbemQuery.Property(status, "CacheSizeBytes")), out hr, ct) switch
            {
                null when hr == WbemConnection.Unfinished => DeliveryOptimizationReading.Unanswered(
                    $"Delivery Optimization did not answer within {ToolRun.QueryLimit.TotalMinutes:0} minutes."),
                null => Unanswered(hr),
                [] => DeliveryOptimizationReading.Unanswered("Windows gave no figure."),
                [var reading] => reading,
                _ => DeliveryOptimizationReading.Unanswered(
                    "Windows reported more than one Delivery Optimization status, so the figure is not known."),
            };
        }
        finally
        {
            Marshal.ReleaseComObject(services);
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
