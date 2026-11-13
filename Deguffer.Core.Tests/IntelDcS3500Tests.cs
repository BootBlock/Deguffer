using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: Intel's DC S3500 specification, 328860-008US table 18, is read on the DC S3500 and on nothing
/// else. Each reading has a covered drive, and a DC S3510 just outside the key keeps the catalogue's
/// reading. Every model name and every byte is invented.
/// </summary>
public sealed class IntelDcS3500Tests
{
    private const string Covered = "INTEL SSDSC2BB999G4";
    private const string Outside = "INTEL SSDSC2BB999G6";

    /// <summary>The largest raw value six bytes hold.</summary>
    private const ulong LargestRaw = 0xFFFF_FFFF_FFFF;

    private static HealthAttribute Row(byte id, ulong raw = 0, byte value = 100, string model = Covered, bool preFail = false, byte? threshold = null) =>
        Assert.Single(AtaAttributeRows.Of([new AtaAttribute(id, preFail, value, value, raw, threshold)], AtaDrives.Named(model), logicalSectorBytes: null));

    private static string Reading(byte id, ulong raw, string model = Covered) => Row(id, raw, model: model).Reading;

    /// <summary>Bytes laid out least significant first, as the drive reports a raw value.</summary>
    private static ulong Raw(params byte[] bytes) => bytes.Select((b, index) => (ulong)b << (8 * index)).Aggregate(0UL, (sum, part) => sum | part);

    [Theory]
    [InlineData(170, "Spare blocks left")]
    [InlineData(171, "Program failures")]
    [InlineData(172, "Erase failures")]
    [InlineData(175, "Power-loss capacitor test")]
    [InlineData(183, "SATA speed drops")]
    [InlineData(190, "Case temperature")]
    [InlineData(192, "Unsafe shutdowns")]
    [InlineData(194, "Temperature")]
    [InlineData(225, "Data written by the computer")]
    [InlineData(226, "Wear in the timed workload")]
    [InlineData(227, "Reads in the timed workload")]
    [InlineData(228, "Workload timer")]
    [InlineData(232, "Spare blocks left")]
    [InlineData(233, "Media wear indicator")]
    [InlineData(234, "Thermal throttling")]
    [InlineData(241, "Total data written")]
    [InlineData(242, "Total data read")]
    public void EveryAttributeTheSpecificationDefinesIsNamedOnTheDcS3500(byte id, string name) =>
        Assert.Equal(name, Row(id).Name);

    [Theory]
    [InlineData(175, "Vendor-specific attribute 175")]
    [InlineData(190, "Airflow temperature")]
    [InlineData(192, "Emergency head retracts")]
    [InlineData(225, "Vendor-specific attribute 225")]
    [InlineData(226, "Load time")]
    [InlineData(228, "Vendor-specific attribute 228")]
    [InlineData(234, "Vendor-specific attribute 234")]
    public void AFamilyJustOutsideKeepsTheCataloguesReading(byte id, string name) =>
        Assert.Equal(name, Row(id, model: Outside).Name);

    [Theory]
    [InlineData(225)]
    [InlineData(241)]
    [InlineData(242)]
    public void DataIsCountedIn32MiBUnits(byte id)
    {
        Assert.Equal("32.0 MB", Reading(id, 1));
        Assert.Equal("1.0 GB", Reading(id, 32));
        Assert.Equal("1.0 TB", Reading(id, 32_768));
    }

    /// <summary>The count fills all six bytes, so a figure above the low four is a real total, not another field.</summary>
    [Theory]
    [InlineData(225)]
    [InlineData(241)]
    [InlineData(242)]
    public void A32MiBCountFillsAllSixBytes(byte id) =>
        Assert.Equal("128 PB", Reading(id, 1UL << 32));

    /// <summary>(2^48 - 1) units of 2^25 bytes is just under 8 ZiB, far past the largest long.</summary>
    [Theory]
    [InlineData(225)]
    [InlineData(241)]
    [InlineData(242)]
    public void TheLargestRawValueIsStatedInFull(byte id) =>
        Assert.Equal("8.0 ZB", Reading(id, LargestRaw));

    [Fact]
    public void DataReadStaysAsReportedOutsideTheFamily() =>
        Assert.Equal("Scored 100", Reading(242, 32, Outside));

    [Theory]
    [InlineData(0UL, "0.000%")]
    [InlineData(4_450UL, "4.345%")]
    [InlineData(102_400UL, "100.000%")]
    [InlineData(0xFFFFUL, "Not measured yet")]
    public void TimedWorkloadWearIsTheRawValueOver1024(ulong raw, string expected) =>
        Assert.Equal(expected, Reading(226, raw));

    [Theory]
    [InlineData(0UL, "0%")]
    [InlineData(75UL, "75%")]
    [InlineData(0xFFFFUL, "Not measured yet")]
    public void TimedWorkloadReadsAreAWholePercentage(ulong raw, string expected) =>
        Assert.Equal(expected, Reading(227, raw));

    [Theory]
    [InlineData(1UL, "1 minute")]
    [InlineData(45UL, "45 minutes")]
    [InlineData(90UL, "90 minutes (1 hour)")]
    [InlineData(3_000UL, "3,000 minutes (2 days)")]
    [InlineData(0x1_0000_0005UL, "5 minutes")]
    public void TheWorkloadTimerIsInMinutesInTheLowFourBytes(ulong raw, string expected) =>
        Assert.Equal(expected, Reading(228, raw));

    [Fact]
    public void CaseTemperaturePacksItsRangeInSingleBytesAndCountsOverheating()
    {
        Assert.Equal("35 °C (lowest 20 °C, highest 45 °C, over its rated limit 2 times)", Reading(190, Raw(35, 0, 20, 45, 2, 0)));
        Assert.Equal("35 °C (lowest 20 °C, highest 45 °C, over its rated limit 1 time)", Reading(190, Raw(35, 0, 20, 45, 1, 0)));
        Assert.Equal("35 °C (lowest 20 °C, highest 45 °C)", Reading(190, Raw(35, 0, 20, 45, 0, 0)));
        Assert.Equal("Not reported", Reading(190, 0));
        Assert.Equal(AttributeStanding.Normal, Row(190, Raw(35, 0, 20, 45, 2, 0)).Standing);
    }

    [Fact]
    public void InternalTemperatureIsTheLowByte() =>
        Assert.Equal("41 °C", Reading(194, Raw(41, 0, 13, 0, 50, 0)));

    [Fact]
    public void ACapacitorTestStatesItsTimeItsAgeAndHowManyThereHaveBeen()
    {
        Assert.Equal("Last test 1,200 µs, 30 minutes ago, 5 tests in all", Reading(175, Raw(0xB0, 0x04, 30, 0, 5, 0)));
        Assert.Equal("Last test 1,200 µs, 30 minutes ago, 1 test in all", Reading(175, Raw(0xB0, 0x04, 30, 0, 1, 0)));
        Assert.Equal("No test recorded", Reading(175, Raw(0, 0, 0, 0, 0, 0)));
    }

    /// <summary>A result below 25 is an error code, and a field at FFFFh has stopped counting.</summary>
    [Fact]
    public void ACapacitorTestReadsAnErrorCodeAndASaturatedFieldAsSuch()
    {
        Assert.Equal("Last test failed with code 24, 30 minutes ago, 5 tests in all", Reading(175, Raw(24, 0, 30, 0, 5, 0)));
        Assert.Equal("Last test 25 µs, 30 minutes ago, 5 tests in all", Reading(175, Raw(25, 0, 30, 0, 5, 0)));
        Assert.Equal(
            "Last test 65,535 µs or more, 65,535 minutes (45 days) or more ago, 65,535 tests or more in all",
            Reading(175, LargestRaw));
    }

    [Fact]
    public void AFailedCapacitorTestFailsByTheDrivesOwnThreshold() =>
        Assert.Equal(AttributeStanding.Failing, Row(175, Raw(3, 0, 30, 0, 5, 0), value: 1, preFail: true, threshold: 10).Standing);

    [Theory]
    [InlineData(0UL, "Never throttled")]
    [InlineData(0x0000_0000_0100UL, "Not throttling, 1 time in all")]
    [InlineData(0x0000_0000_0328UL, "Throttling 40%, 3 times in all")]
    [InlineData(0xAB00_0000_0100UL, "Not throttling, 1 time in all")]
    public void ThrottlingIsAPercentageInByteZeroAndACountInBytesOneToFour(ulong raw, string expected) =>
        Assert.Equal(expected, Reading(234, raw));

    [Theory]
    [InlineData(171)]
    [InlineData(172)]
    public void FlashFailuresAreCountedFromTheRawValue(byte id)
    {
        Assert.Equal(("None", AttributeStanding.Normal), (Row(id, 0).Reading, Row(id, 0).Standing));
        Assert.Equal(("2", AttributeStanding.Watch), (Row(id, 2).Reading, Row(id, 2).Standing));
    }

    [Theory]
    [InlineData(170)]
    [InlineData(232)]
    public void SpareBlocksAreReadByTheirNormalisedValue(byte id)
    {
        Assert.Equal(("50% left", AttributeStanding.Normal), (Row(id, 7, value: 50).Reading, Row(id, 7, value: 50).Standing));
        Assert.Equal(AttributeStanding.Watch, Row(id, 7, value: 10).Standing);
    }

    [Theory]
    [InlineData(183)]
    [InlineData(192)]
    public void CountsAreReadFromTheRawValue(byte id) =>
        Assert.Equal(("7", AttributeStanding.Normal), (Row(id, 7).Reading, Row(id, 7).Standing));
}
