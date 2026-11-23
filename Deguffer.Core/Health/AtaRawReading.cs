using System.Globalization;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// An ATA attribute's raw bytes in words, by the format its definition names. Whether the reading is a
/// concern is decided by <see cref="AtaAttributeRows"/>, not here.
/// </summary>
internal static class AtaRawReading
{
    private const ulong LowDoubleWord = 0xFFFF_FFFF;
    private const ulong LowByte = 0xFF;
    private const int BitsPerByte = 8;

    /// <summary>
    /// The value Intel's DC specifications (328860, 332211 and 334826, section 5.4.2) reset E2h, E3h and E4h
    /// to, and hold E2h and E3h at until the workload timer reaches 60 minutes.
    /// </summary>
    private const ulong NotYetMeasured = 0xFFFF;

    /// <summary>What a two-byte field of the capacitor test holds at once it can count no higher (328860, 332211 and 334826, AFh).</summary>
    private const ulong Saturated = 0xFFFF;

    /// <summary>The lowest capacitor test result that is a time. Anything lower is an error code (328860, 332211 and 334826, AFh).</summary>
    private const ulong LowestDischargeMicroseconds = 25;

    /// <summary>
    /// The highest throttling status that is a percentage. 332211 and 334826 call anything larger invalid,
    /// and 328860 gives the status as a percentage, so nothing larger is one there either.
    /// </summary>
    private const ulong FullThrottle = 100;

    private const ulong RawPerPercent = 1024;
    private const ulong SectorsPer32MiB = 65_536;

    private const string NotReported = "Not reported";
    private const string NotMeasured = "Not measured yet";

    private static readonly DecimalPlaces Hundredths = new(2, 100);
    private static readonly DecimalPlaces Thousandths = new(3, 1000);

    /// <summary>The count, hours or minutes in the raw value, as wide as the definition says the figure is.</summary>
    internal static ulong Figure(AtaAttribute attribute, AtaAttributeDefinition known) =>
        known.Width is RawWidth.AllSixBytes ? attribute.Raw : attribute.Raw & LowDoubleWord;

    internal static string Code(AtaAttribute attribute) => $"Code 0x{attribute.Raw:X2}";

    /// <summary>The percentage a <see cref="SmartRawFormat.Percent"/> or <see cref="SmartRawFormat.PercentInLowByte"/> attribute reports.</summary>
    internal static int Percentage(AtaAttribute attribute, AtaAttributeDefinition known) =>
        known.Format is SmartRawFormat.PercentInLowByte ? (int)(attribute.Raw & LowByte) : attribute.Value;

    internal static string Temperature(AtaAttribute attribute, SmartRawFormat format) => format switch
    {
        SmartRawFormat.CelsiusRange => Celsius(Field(attribute, 0, 2), Field(attribute, 2, 2), Field(attribute, 4, 2), overheats: 0),
        SmartRawFormat.CelsiusRangeHighestFirst => Celsius(Field(attribute, 0, 2), Field(attribute, 4, 2), Field(attribute, 2, 2), overheats: 0),
        SmartRawFormat.CelsiusWithOverheats => Celsius(Field(attribute, 0, 2), Field(attribute, 2, 1), Field(attribute, 3, 1), Field(attribute, 4, 2)),
        _ => Field(attribute, 0, 1) is var celsius and not 0 ? HealthText.Temperature((int)celsius) : NotReported,
    };

    internal static string Decoded(AtaAttribute attribute, AtaAttributeDefinition known, uint? logicalSectorBytes) => known.Format switch
    {
        SmartRawFormat.Hours => HealthText.Hours(Figure(attribute, known)),
        SmartRawFormat.Minutes => Measured(Figure(attribute, known), minutes => HealthText.Minutes(minutes)),
        SmartRawFormat.Bytes512 => HealthText.Size((UInt128)attribute.Raw * DriveRequests.SectorBytes),
        SmartRawFormat.LogicalSectors => logicalSectorBytes is { } size
            ? HealthText.Size((UInt128)attribute.Raw * size)
            : $"{HealthText.Count(attribute.Raw)} sectors",
        SmartRawFormat.Units32MiB => HealthText.Size((UInt128)Figure(attribute, known) * SectorsPer32MiB * DriveRequests.SectorBytes),
        SmartRawFormat.Percent or SmartRawFormat.PercentInLowByte => HealthText.Percent(Percentage(attribute, known)),
        SmartRawFormat.PercentPer1024ToThousandths => Measured(Figure(attribute, known), static raw => Per1024(raw, Thousandths)),
        SmartRawFormat.PercentPer1024ToHundredths => Measured(Figure(attribute, known), static raw => Per1024(raw, Hundredths)),
        SmartRawFormat.RawPercent => Measured(Figure(attribute, known), percent => $"{HealthText.Count(percent)}%"),
        SmartRawFormat.CapacitorTest => CapacitorTest(attribute),
        SmartRawFormat.ThrottleStatus => Throttle(attribute),
        SmartRawFormat.Status => Code(attribute),
        _ => HealthText.Count(Figure(attribute, known)),
    };

    /// <summary>The <paramref name="length"/> bytes of the raw value from byte <paramref name="first"/>, least significant first.</summary>
    private static ulong Field(AtaAttribute attribute, int first, int length) =>
        (attribute.Raw >> (first * BitsPerByte)) & ((1UL << (length * BitsPerByte)) - 1);

    /// <summary>A drive that has recorded no highest has recorded no range either.</summary>
    private static string Celsius(ulong current, ulong lowest, ulong highest, ulong overheats)
    {
        if (current == 0)
        {
            return NotReported;
        }

        var range = highest == 0 ? null : $"lowest {HealthText.Temperature((int)lowest)}, highest {HealthText.Temperature((int)highest)}";
        var hot = overheats == 0 ? null : $"over its rated limit {Counted.Of(overheats, "time", "times")}";
        var detail = range is null ? hot : hot is null ? range : $"{range}, {hot}";

        return detail is null ? HealthText.Temperature((int)current) : $"{HealthText.Temperature((int)current)} ({detail})";
    }

    private static string Measured(ulong figure, Func<ulong, string> state) => figure == NotYetMeasured ? NotMeasured : state(figure);

    /// <summary>
    /// The raw value over 1024 as a percentage, cut to <paramref name="cut"/>, as section 5.4.2 of 328860
    /// (three places) and of 332211, 334826 and 334827 (two) work their examples.
    /// </summary>
    private static string Per1024(ulong raw, DecimalPlaces cut)
    {
        var parts = (UInt128)raw * cut.PartsPerUnit / RawPerPercent;
        var fraction = ((ulong)(parts % cut.PartsPerUnit)).ToString(CultureInfo.InvariantCulture).PadLeft(cut.Places, '0');

        return $"{HealthText.Count(parts / cut.PartsPerUnit)}.{fraction}%";
    }

    private static string CapacitorTest(AtaAttribute attribute)
    {
        var (result, minutes, tests) = (Field(attribute, 0, 2), Field(attribute, 2, 2), Field(attribute, 4, 2));

        if (tests == 0)
        {
            return "No test recorded";
        }

        var outcome = result < LowestDischargeMicroseconds
            ? $"Last test failed with code {result}"
            : $"Last test {AtLeast(result, $"{HealthText.Count(result)} µs")}";

        return $"{outcome}, {AtLeast(minutes, HealthText.Minutes(minutes))} ago, {AtLeast(tests, Counted.Of(tests, "test", "tests"))} in all";
    }

    private static string AtLeast(ulong field, string text) => field == Saturated ? $"{text} or more" : text;

    private static string Throttle(AtaAttribute attribute)
    {
        var (percent, events) = (Field(attribute, 0, 1), Field(attribute, 1, 4));

        return (percent, events) switch
        {
            (0, 0) => "Never throttled",
            (0, _) => $"Not throttling, {Counted.Of(events, "time", "times")} in all",
            (> FullThrottle, _) => $"Unknown throttling status {percent}, {Counted.Of(events, "time", "times")} in all",
            _ => $"Throttling {percent}%, {Counted.Of(events, "time", "times")} in all",
        };
    }

    /// <summary>How many decimal places a percentage is cut to, and the parts of one percent that makes.</summary>
    private readonly record struct DecimalPlaces(int Places, ulong PartsPerUnit);
}
