using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// An attribute's raw bytes in words, laid out as a drive database's <c>-v</c> option says. It states
/// the figures and their units and nothing about whether they are a concern, because the database is
/// not a manufacturer's document (§7.6).
/// </summary>
internal static class DatabaseRawValue
{
    private const string FortyEightBitOrder = "543210";
    private const string FiftySixBitOrder = "r543210";
    private const string SixtyFourBitOrder = "543210wv";

    private const int SecondsPerMinute = 60;
    private const int HalfMinutesPerMinute = 2;
    private const ulong Low16 = 0xFFFF;
    private const ulong Low24 = 0xFF_FFFF;
    private const ulong Low32 = 0xFFFF_FFFF;

    internal static string Text(AtaAttribute attribute, AttributePreset preset)
    {
        var raw = Figure(attribute, preset);

        return preset.Format switch
        {
            DatabaseRawFormat.Raw8 => $"{Byte(raw, 5)} {Byte(raw, 4)} {Byte(raw, 3)} {Byte(raw, 2)} {Byte(raw, 1)} {Byte(raw, 0)}",
            DatabaseRawFormat.Raw16 => $"{Word(raw, 2)} {Word(raw, 1)} {Word(raw, 0)}",
            DatabaseRawFormat.Hex48 => $"0x{raw:X12}",
            DatabaseRawFormat.Hex56 => $"0x{raw:X14}",
            DatabaseRawFormat.Hex64 => $"0x{raw:X16}",
            DatabaseRawFormat.Raw16WithRaw16 => WithExtra(Word(raw, 0), Word(raw, 1) | Word(raw, 2), $"{Word(raw, 2)} {Word(raw, 1)}"),
            DatabaseRawFormat.Raw16WithAverage16 => WithExtra(Word(raw, 0), Word(raw, 1), $"average {HealthText.Count(Word(raw, 1))}"),
            DatabaseRawFormat.Raw24WithRaw8 => WithExtra(raw & Low24, raw >> 24 & Low24, $"{Byte(raw, 5)} {Byte(raw, 4)} {Byte(raw, 3)}"),
            DatabaseRawFormat.Raw24OverRaw24 => $"{HealthText.Count(raw >> 24 & Low24)} / {HealthText.Count(raw & Low24)}",
            DatabaseRawFormat.Raw24OverRaw32 => $"{HealthText.Count(raw >> 32 & Low24)} / {HealthText.Count(raw & Low32)}",
            DatabaseRawFormat.Seconds => HealthText.Minutes(raw / SecondsPerMinute),
            DatabaseRawFormat.Minutes => WithExtra(Word(raw, 0) | (Word(raw, 1) << 16), Word(raw, 2), HealthText.Count(Word(raw, 2)), HealthText.Minutes),
            DatabaseRawFormat.HalfMinutes => HealthText.Minutes(raw / HalfMinutesPerMinute),
            DatabaseRawFormat.HoursAndMilliseconds => HealthText.Hours(raw & Low32),
            DatabaseRawFormat.Celsius => HealthText.Temperature((sbyte)Byte(raw, 0)),
            DatabaseRawFormat.TenthsCelsius => HealthText.TemperatureInTenths((uint)Word(raw, 0)),
            _ => HealthText.Count(raw),
        };
    }

    /// <summary>
    /// The figure built from the bytes the option names, most significant first, or from the format's
    /// own order where it names none: six raw bytes, the reserved byte above them for the 56-bit formats,
    /// and the value and worst value below them for the 64-bit ones.
    /// </summary>
    internal static ulong Figure(AtaAttribute attribute, AttributePreset preset)
    {
        var order = preset.ByteOrder.Length > 0 ? preset.ByteOrder : preset.Format switch
        {
            DatabaseRawFormat.Raw64 or DatabaseRawFormat.Hex64 => SixtyFourBitOrder,
            DatabaseRawFormat.Raw56 or DatabaseRawFormat.Hex56 or DatabaseRawFormat.Raw24OverRaw32 or DatabaseRawFormat.HoursAndMilliseconds => FiftySixBitOrder,
            _ => FortyEightBitOrder,
        };

        ulong figure = 0;

        foreach (var source in order)
        {
            figure = (figure << 8) | source switch
            {
                >= '0' and <= '5' => Byte(attribute.Raw, source - '0'),
                'r' => attribute.Reserved,
                'v' => attribute.Value,
                'w' => attribute.Worst,
                _ => 0UL,
            };
        }

        return figure;
    }

    private static ulong Byte(ulong figure, int index) => figure >> (index * 8) & 0xFF;

    private static ulong Word(ulong figure, int index) => figure >> (index * 16) & Low16;

    /// <summary>A figure, with more in brackets where <paramref name="extra"/> is not zero.</summary>
    private static string WithExtra(ulong figure, ulong extra, string extraText, Func<UInt128, string>? words = null)
    {
        var main = (words ?? HealthText.Count)(figure);

        return extra == 0 ? main : $"{main} ({extraText})";
    }
}
