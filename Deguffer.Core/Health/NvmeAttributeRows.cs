using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>An NVMe drive's health log as rows, each stating what it means and whether it is a concern.</summary>
internal static class NvmeAttributeRows
{
    /// <summary>The drive's own warnings, which are what a Good verdict rests on.</summary>
    internal const string CriticalWarningKey = "critical-warning";

    /// <summary>Critical warning bit 1, the only one that is not about the drive's condition.</summary>
    private const byte TemperatureWarningBit = 0b0000_0010;

    private const int LifeUsedWarningPercent = 90;

    /// <summary>One NVMe data unit is a thousand 512-byte sectors.</summary>
    private const int DataUnitBytes = 512_000;

    private static readonly (byte Bit, string Name)[] Warnings =
    [
        (0b0000_0001, "reserve space is low"),
        (TemperatureWarningBit, "temperature out of range"),
        (0b0000_0100, "reliability reduced"),
        (0b0000_1000, "drive is read-only"),
        (0b0001_0000, "power-loss protection failed"),
        (0b0010_0000, "persistent memory is read-only"),
    ];

    internal static IReadOnlyList<HealthAttribute> Of(NvmeHealthLog log, NvmeController? controller) =>
    [
        CriticalWarning(log.CriticalWarning),
        Temperature(log.TemperatureKelvin, controller),
        new("spare", "Reserve space", HealthText.Percent(log.AvailableSpare), $"Spare memory that replaces worn memory. The drive warns below {log.SpareThreshold}%.", AttributeStanding.Normal, $"{log.AvailableSpare}"),
        LifeUsed(log.PercentageUsed),
        MediaErrors(log.MediaErrors),
        Size("written", "Total data written", log.DataUnitsWritten, "How much the computer has written to the drive over its life."),
        Size("read", "Total data read", log.DataUnitsRead, "How much the computer has read from the drive over its life."),
        new("hours", "Time powered on", HealthText.Hours(log.PowerOnHours), "How long the drive has been switched on over its life.", AttributeStanding.Normal, $"{log.PowerOnHours}"),
        Count("cycles", "Power cycles", log.PowerCycles, "How many times the drive has been switched on."),
        Count("unsafe", "Unexpected power loss", log.UnsafeShutdowns, "Times power was lost without the drive being told first. A few are normal."),
        Count("error-entries", "Errors in the drive's log", log.ErrorLogEntries, "Errors the drive has logged over its life. Many are harmless, so the log alone is not a sign of failure."),
        Count("host-writes", "Write commands", log.HostWrites, "How many write commands the drive has completed."),
        Count("host-reads", "Read commands", log.HostReads, "How many read commands the drive has completed."),
        new("busy", "Time busy", HealthText.Minutes(log.BusyMinutes), "How long the drive has spent working on commands.", AttributeStanding.Normal, $"{log.BusyMinutes}"),
        Count("hot-minutes", "Minutes above warning temperature", log.WarningTemperatureMinutes, "How long the drive has run hotter than its warning limit."),
        Count("critical-minutes", "Minutes above critical temperature", log.CriticalTemperatureMinutes, "How long the drive has run hotter than its critical limit."),
        .. log.SensorKelvin.Select((kelvin, index) => new HealthAttribute(
            $"sensor-{index + 1}",
            $"Temperature sensor {index + 1}",
            HealthText.Temperature(HealthText.Celsius(kelvin)),
            "One of the drive's own temperature sensors.",
            AttributeStanding.Normal,
            $"{kelvin} K")),
    ];

    private static HealthAttribute CriticalWarning(byte warning)
    {
        var named = Warnings.Where(entry => (warning & entry.Bit) != 0).Select(entry => entry.Name).ToList();

        if (named.Count == 0)
        {
            return new HealthAttribute(CriticalWarningKey, "Critical warnings", "None", "The drive reports no critical warnings.", AttributeStanding.Normal, "0");
        }

        var onlyTemperature = warning == TemperatureWarningBit;

        return new HealthAttribute(
            CriticalWarningKey,
            "Critical warnings",
            string.Join(", ", named),
            onlyTemperature
                ? "The drive is outside its safe temperature range. Improve its cooling."
                : "The drive reports a serious problem. Copy your important files somewhere else now.",
            onlyTemperature ? AttributeStanding.Watch : AttributeStanding.Failing,
            $"{warning}");
    }

    private static HealthAttribute Temperature(int kelvin, NvmeController? controller)
    {
        // A controller reports 0 where it has no warning temperature.
        var isHot = controller is { WarningKelvin: > 0 and var limit } && kelvin >= limit;

        return new HealthAttribute(
            "temperature",
            "Temperature",
            HealthText.Temperature(HealthText.Celsius(kelvin)),
            isHot ? "The drive is at or above the temperature it warns at. Improve its cooling." : "The drive's overall temperature.",
            isHot ? AttributeStanding.Watch : AttributeStanding.Normal,
            $"{kelvin} K");
    }

    private static HealthAttribute LifeUsed(byte percent) => new(
        "life-used",
        "Rated life used",
        HealthText.Percent(percent),
        percent >= LifeUsedWarningPercent
            ? "The drive is near or past its rated write life. Drives often continue past 100%, but plan to replace it."
            : "How much of the manufacturer's rated write life is used. Drives often continue past 100%.",
        percent >= LifeUsedWarningPercent ? AttributeStanding.Watch : AttributeStanding.Normal,
        $"{percent}");

    private static HealthAttribute MediaErrors(UInt128 count) => new(
        "media-errors",
        "Media errors",
        count == 0 ? "None" : HealthText.Count(count),
        count == 0
            ? "The drive has found no data it could not read or verify."
            : "The drive has found data it could not read or verify. Back up important files.",
        count == 0 ? AttributeStanding.Normal : AttributeStanding.Watch,
        $"{count}");

    private static HealthAttribute Size(string key, string name, UInt128 units, string meaning) =>
        new(key, name, HealthText.Size(units * DataUnitBytes), meaning, AttributeStanding.Normal, $"{units} data units");

    private static HealthAttribute Count(string key, string name, UInt128 count, string meaning) =>
        new(key, name, HealthText.Count(count), meaning, AttributeStanding.Normal, $"{count}");
}
