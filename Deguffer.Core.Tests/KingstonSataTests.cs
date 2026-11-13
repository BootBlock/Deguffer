using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: each Kingston document is read only on the models it names, and each by its own layout. The
/// bytes are synthesised so that reading another document's layout gives a different answer. Every
/// model name is invented: a documented part number with a capacity no drive has.
/// </summary>
public sealed class KingstonSataTests
{
    private const string AsReported = "Scored 50";

    private const string Kc100 = "KINGSTON SKC100S3999G";
    private const string Kc300 = "KINGSTON SKC300S37A999G";
    private const string Uv500 = "KINGSTON SUV500999G";
    private const string Kc600 = "KINGSTON SKC600999G";
    private const string Dc500R = "KINGSTON SEDC500R999G";
    private const string Kc400 = "KINGSTON SKC400S37999G";

    /// <summary>A SandForce SF-2000 model that MKP-306.2 does not name.</summary>
    private const string V300 = "KINGSTON SV300S37A999G";

    /// <summary>A Kingston model that no document covers.</summary>
    private const string A400 = "KINGSTON SA400S37999G";

    /// <summary>Byte 0 is 42 and the bytes above it are not, so only a reading of byte 0 alone gives 42.</summary>
    private const ulong FortyTwoInByteZero = 0x0102_0304_052A;

    /// <summary>The low four bytes hold 1,024 and byte 4 holds 1, so a six-byte reading gives far more.</summary>
    private const ulong KibiInLowFourBytes = 0x0001_0000_0400;

    /// <summary>30 °C now, with 60 °C in bytes 2-3 and 20 °C in bytes 4-5.</summary>
    private const ulong HighestInBytesTwoAndThree = 0x0014_003C_001E;

    /// <summary>30 °C now, with 20 °C in bytes 2-3 and 60 °C in bytes 4-5.</summary>
    private const ulong LowestInBytesTwoAndThree = 0x003C_0014_001E;

    private const string ThirtyWithItsRange = "30 °C (lowest 20 °C, highest 60 °C)";

    public static TheoryData<string> Phison2016Models =>
    [
        "KINGSTON SKC310S37A999G",
        "KINGSTON SUV300S37A999G",
        Kc400,
        "KINGSTON SHSS37A999G",
        "KINGSTON SV310S37A999G",
        "KINGSTON SM2280S3G2999G",
        "KINGSTON SEDC400S37999G",
    ];

    public static TheoryData<string> Phison2023Models =>
    [
        "KINGSTON SEDC450R999G",
        Dc500R,
        "KINGSTON SEDC500M999G",
        "KINGSTON SEDC600M999G",
    ];

    private static HealthAttribute Row(byte id, string model, ulong raw = 0, byte value = 50, uint? logicalSectorBytes = null) =>
        Assert.Single(AtaAttributeRows.Of([new AtaAttribute(id, false, value, value, raw, null)], AtaDrives.Named(model), logicalSectorBytes));

    private static string Reading(byte id, string model, ulong raw = 0) => Row(id, model, raw).Reading;

    [Theory]
    [InlineData(Kc100)]
    [InlineData(Kc300)]
    [InlineData(Uv500)]
    [InlineData("KINGSTON SUV500M8999G")]
    [InlineData("KINGSTON SUV500MS999G")]
    public void LifeLeftIsTheNormalisedValueOnMkp306AndTheUv500Sheet(string model) =>
        Assert.Equal("50% left", Reading(231, model, FortyTwoInByteZero));

    [Theory]
    [MemberData(nameof(Phison2016Models))]
    [MemberData(nameof(Phison2023Models))]
    public void LifeLeftIsRawByteZeroOnMkp521(string model) =>
        Assert.Equal("42% left", Reading(231, model, FortyTwoInByteZero));

    [Theory]
    [InlineData(10, AttributeStanding.Watch)]
    [InlineData(11, AttributeStanding.Normal)]
    public void LifeLeftInRawByteZeroWarnsByThatByteNotTheNormalisedValue(ulong percent, AttributeStanding expected)
    {
        Assert.Equal(expected, Row(231, Kc400, raw: percent, value: 100).Standing);
        Assert.Equal(expected, Row(231, Dc500R, raw: percent, value: 100).Standing);
    }

    [Theory]
    [InlineData(Kc600)]
    [InlineData("KINGSTON SKC600MS999G")]
    [InlineData(V300)]
    [InlineData(A400)]
    [InlineData("XSKC310S37A999G")]
    [InlineData("KINGSTON SUV500B999G")]
    [InlineData("KINGSTON SEDC1000B999G")]
    [InlineData("KINGSTON SEDC600ME999G")]
    public void LifeLeftIsLeftUninterpretedWhereNoDocumentDefinesIt(string model) =>
        Assert.Equal(AsReported, Reading(231, model, FortyTwoInByteZero));

    [Theory]
    [InlineData(241, Kc100)]
    [InlineData(242, Kc300)]
    [InlineData(241, Uv500)]
    [InlineData(242, Uv500)]
    public void DataTransferredIsGigabytesInTheLowFourBytesOnMkp306AndTheUv500Sheet(byte id, string model) =>
        Assert.Equal("1,024 GB", Reading(id, model, KibiInLowFourBytes));

    [Theory]
    [InlineData(241, Kc400)]
    [InlineData(242, Kc400)]
    [InlineData(241, Dc500R)]
    [InlineData(242, Dc500R)]
    public void DataTransferredIsGigabytesInAllSixBytesOnMkp521(byte id, string model) =>
        Assert.Equal("4,294,968,320 GB", Reading(id, model, KibiInLowFourBytes));

    [Theory]
    [InlineData(241)]
    [InlineData(242)]
    public void GigabytesNameTheUnitTheDocumentLeavesUndefined(byte id) =>
        Assert.Contains("1,024 megabytes", Row(id, Dc500R).Meaning, StringComparison.Ordinal);

    [Fact]
    public void Mkp306DataReadNamesItsStepOf64Gigabytes() =>
        Assert.Contains("steps of 64 GB", Row(242, Kc100).Meaning, StringComparison.Ordinal);

    [Theory]
    [InlineData(241)]
    [InlineData(242)]
    public void Kc600DataTransferredIsLogicalSectors(byte id)
    {
        Assert.Equal("1.0 MB", Row(id, Kc600, raw: 2048, logicalSectorBytes: 512).Reading);
        Assert.Equal("2,048 sectors", Row(id, Kc600, raw: 2048).Reading);
    }

    [Theory]
    [InlineData(241, V300)]
    [InlineData(242, A400)]
    public void DataTransferredStaysAsReportedOnModelsNoDocumentNames(byte id, string model) =>
        Assert.Equal(AsReported, Reading(id, model, KibiInLowFourBytes));

    [Fact]
    public void Mkp306TemperaturePutsTheHighestBeforeTheLowest() =>
        Assert.Equal(ThirtyWithItsRange, Reading(194, Kc100, HighestInBytesTwoAndThree));

    [Theory]
    [InlineData(Uv500)]
    [InlineData(Kc400)]
    [InlineData(Dc500R)]
    public void OtherDocumentsPutTheLowestTemperatureBeforeTheHighest(string model) =>
        Assert.Equal(ThirtyWithItsRange, Reading(194, model, LowestInBytesTwoAndThree));

    [Theory]
    [InlineData(Kc600)]
    [InlineData(V300)]
    public void TemperatureIsTheLowByteWhereNoDocumentGivesItsLayout(string model) =>
        Assert.Equal("30 °C", Reading(194, model, LowestInBytesTwoAndThree));

    [Fact]
    public void Mkp306CountsTheSpreadOfWearAsAPercentage()
    {
        var row = Row(177, Kc300, raw: 0x0005_0000_0002);

        Assert.Equal("Wear range", row.Name);
        Assert.Equal(("2%", AttributeStanding.Normal), (row.Reading, row.Standing));
    }

    [Theory]
    [InlineData(Uv500)]
    [InlineData(Kc400)]
    [InlineData(V300)]
    public void WearSpreadIsReadOnlyOnMkp306Models(string model) =>
        Assert.Equal(AsReported, Reading(177, model, raw: 2));
}
