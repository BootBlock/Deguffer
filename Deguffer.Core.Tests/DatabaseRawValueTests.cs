using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>#751: a drive database's raw formats and byte orders, each against bytes worked out by hand.</summary>
public sealed class DatabaseRawValueTests
{
    /// <summary>Raw bytes 0 to 5 are 01 to 06, least significant first. The value is 0x64, the worst 0x63 and the reserved byte 0x07.</summary>
    private static readonly AtaAttribute Counting = new(9, false, 0x64, 0x63, 0x06_05_04_03_02_01UL, null, 0x07);

    private static string Text(DatabaseRawFormat format, ulong raw, string order = "") =>
        DatabaseRawValue.Text(Counting with { Raw = raw }, new AttributePreset(format, order, null, DriveMedium.Any));

    private static ulong Figure(DatabaseRawFormat format, string order = "") =>
        DatabaseRawValue.Figure(Counting, new AttributePreset(format, order, null, DriveMedium.Any));

    [Fact]
    public void FortyEightBitFormatsTakeTheSixRawBytes() => Assert.Equal(0x06_05_04_03_02_01UL, Figure(DatabaseRawFormat.Raw48));

    [Fact]
    public void FiftySixBitFormatsPutTheReservedByteAboveThem() => Assert.Equal(0x07_06_05_04_03_02_01UL, Figure(DatabaseRawFormat.Raw56));

    [Fact]
    public void SixtyFourBitFormatsPutTheValueAndWorstBelowThem() =>
        Assert.Equal(0x06_05_04_03_02_01_63_64UL, Figure(DatabaseRawFormat.Hex64));

    [Fact]
    public void AByteOrderNamesTheBytesMostSignificantFirst()
    {
        Assert.Equal(0x01_02_03_04_05_06UL, Figure(DatabaseRawFormat.Raw48, "012345"));
        Assert.Equal(0x00_06_00_02_01UL, Figure(DatabaseRawFormat.Raw48, "z5z10"));
        Assert.Equal(0x64_63_07UL, Figure(DatabaseRawFormat.Raw48, "vwr"));
    }

    [Fact]
    public void EachFormatInWords()
    {
        Assert.Equal("6 5 4 3 2 1", Text(DatabaseRawFormat.Raw8, 0x06_05_04_03_02_01UL));
        Assert.Equal("1541 1027 513", Text(DatabaseRawFormat.Raw16, 0x06_05_04_03_02_01UL));
        Assert.Equal("1,234,567", Text(DatabaseRawFormat.Raw48, 1_234_567));
        Assert.Equal("0x0000000000FF", Text(DatabaseRawFormat.Hex48, 0xFF));
        Assert.Equal("0x07000000000001", Text(DatabaseRawFormat.Hex56, 1));
        Assert.Equal("12", Text(DatabaseRawFormat.Raw16WithRaw16, 12));
        Assert.Equal("12 (3 2)", Text(DatabaseRawFormat.Raw16WithRaw16, 0x0003_0002_000CUL));
        Assert.Equal("700 (average 650)", Text(DatabaseRawFormat.Raw16WithAverage16, 0x0000_028A_02BCUL));
        Assert.Equal("5", Text(DatabaseRawFormat.Raw24WithRaw8, 5));
        Assert.Equal("5 (3 2 1)", Text(DatabaseRawFormat.Raw24WithRaw8, 0x03_02_01_000005UL));
        Assert.Equal("10 / 7", Text(DatabaseRawFormat.Raw24OverRaw24, 0x00000A_000007UL));
        Assert.Equal("37 °C", Text(DatabaseRawFormat.Celsius, 0x0000_2D_14_00_25UL));
        Assert.Equal("-5 °C", Text(DatabaseRawFormat.Celsius, 0xFB));
        Assert.Equal("41.5 °C", Text(DatabaseRawFormat.TenthsCelsius, 415));
    }

    [Fact]
    public void ACountOverACountTakesTheReservedByte() =>
        Assert.Equal("460,293 / 3", DatabaseRawValue.Text(
            new AtaAttribute(1, false, 100, 100, 0x0605_0000_0003UL, null, 0x07),
            new AttributePreset(DatabaseRawFormat.Raw24OverRaw32, string.Empty, null, DriveMedium.Any)));

    [Fact]
    public void TimesAreStatedInHoursAndDays()
    {
        Assert.Equal("90 minutes (1 hour)", Text(DatabaseRawFormat.Minutes, 90));
        Assert.Equal("90 minutes (1 hour) (2)", Text(DatabaseRawFormat.Minutes, 0x0002_0000_005AUL));
        Assert.Equal("90 minutes (1 hour)", Text(DatabaseRawFormat.Seconds, 5_400));
        Assert.Equal("90 minutes (1 hour)", Text(DatabaseRawFormat.HalfMinutes, 180));
        Assert.Equal("48 hours (2 days)", DatabaseRawValue.Text(
            new AtaAttribute(9, false, 100, 100, 0x0000_0000_0030UL, null, 0x00),
            new AttributePreset(DatabaseRawFormat.HoursAndMilliseconds, string.Empty, null, DriveMedium.Any)));
    }
}
