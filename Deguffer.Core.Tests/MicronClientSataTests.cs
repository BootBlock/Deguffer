using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: Micron TN-FD-22 is read on the client SATA families it lists, and on nothing else. Each rule
/// has a covered drive and one just outside the key, an MX500, which must keep the catalogue's reading.
/// Every model name and every byte is invented.
/// </summary>
public sealed class MicronClientSataTests
{
    private const string Covered = "Crucial_CT999MX200SSD1";
    private const string CoveredFirmware = "MU01";
    private const string Outside = "CT999MX500SSD1";

    private static AtaAttribute Attribute(byte id, byte value = 100, ulong raw = 0, bool preFail = false, byte? threshold = null, byte worst = 100) =>
        new(id, preFail, value, worst, raw, threshold);

    private static IReadOnlyList<HealthAttribute> Rows(string model, uint? logicalSectorBytes, params AtaAttribute[] attributes) =>
        [.. AtaAttributeRows.Of(attributes, AtaDrives.Named(model, model == Covered ? CoveredFirmware : "M3TEST"), logicalSectorBytes)];

    private static HealthAttribute Row(AtaAttribute attribute, string model = Covered, uint? logicalSectorBytes = null) =>
        Assert.Single(Rows(model, logicalSectorBytes, attribute));

    [Theory]
    [InlineData(1, "Read error events")]
    [InlineData(5, "Retired flash blocks")]
    [InlineData(9, "Time powered on")]
    [InlineData(12, "Power cycles")]
    [InlineData(171, "Program failures")]
    [InlineData(172, "Erase failures")]
    [InlineData(173, "Average erase count")]
    [InlineData(174, "Unexpected power loss")]
    [InlineData(180, "Spare blocks left")]
    [InlineData(183, "SATA speed drops")]
    [InlineData(184, "Data path errors")]
    [InlineData(187, "Reported uncorrectable errors")]
    [InlineData(194, "Temperature")]
    [InlineData(196, "Blocks failed in use")]
    [InlineData(197, "Pending read errors")]
    [InlineData(198, "Errors in the last offline scan")]
    [InlineData(199, "Interface errors")]
    [InlineData(202, "Remaining life")]
    [InlineData(206, "Write failures")]
    [InlineData(210, "Pages rebuilt from parity")]
    [InlineData(246, "Total data written")]
    [InlineData(247, "Flash pages written for the computer")]
    [InlineData(248, "Flash pages written by the drive")]
    public void EveryAttributeInTheNotesTableIsNamedOnACoveredDrive(byte id, string name) =>
        Assert.Equal(name, Row(Attribute(id)).Name);

    [Theory]
    [InlineData(173, "Wear levelling")]
    [InlineData(206, "Vendor-specific attribute 206")]
    [InlineData(210, "Vendor-specific attribute 210")]
    [InlineData(246, "Vendor-specific attribute 246")]
    [InlineData(247, "Vendor-specific attribute 247")]
    [InlineData(248, "Vendor-specific attribute 248")]
    public void AFamilyJustOutsideTheNoteKeepsTheCataloguesReading(byte id, string name) =>
        Assert.Equal(name, Row(Attribute(id), Outside).Name);

    [Fact]
    public void AnM500BeforeFirmwareMu03IsNotCovered()
    {
        Assert.Equal("Vendor-specific attribute 246", Assert.Single(AtaAttributeRows.Of([Attribute(246)], AtaDrives.Named("Crucial_CT999M500SSD1", "MU02"), null)).Name);
        Assert.Equal("Total data written", Assert.Single(AtaAttributeRows.Of([Attribute(246)], AtaDrives.Named("Crucial_CT999M500SSD1", "MU03"), null)).Name);
    }

    [Fact]
    public void ACountFillsAllSixBytes()
    {
        var row = Row(Attribute(5, raw: 0x0102_0000_0000));

        Assert.Equal("1,108,101,562,368", row.Reading);
        Assert.Equal(AttributeStanding.Watch, row.Standing);

        // The same bytes outside the key are a packed figure above a count of none.
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(5, raw: 0x0102_0000_0000), Outside).Standing);
    }

    [Fact]
    public void TheTemperatureStatesTheLowestAndHighestTheDriveRecorded()
    {
        const ulong raw = 0x003D_0014_0026;

        Assert.Equal("38 °C (lowest 20 °C, highest 61 °C)", Row(Attribute(194, raw: raw)).Reading);
        Assert.Equal("38 °C", Row(Attribute(194, raw: raw), Outside).Reading);
    }

    [Fact]
    public void ATemperatureWithNoRecordedHighestStatesOnlyTheCurrent() =>
        Assert.Equal("38 °C", Row(Attribute(194, raw: 0x0000_0014_0026)).Reading);

    [Theory]
    [InlineData(512u, "1.0 MB")]
    [InlineData(4096u, "8.0 MB")]
    [InlineData(null, "2,048 sectors")]
    public void HostWritesCountLogicalSectorsOfTheSizeWindowsReports(uint? logicalSectorBytes, string expected) =>
        Assert.Equal(expected, Row(Attribute(246, raw: 2048), logicalSectorBytes: logicalSectorBytes).Reading);

    [Fact]
    public void RemainingLifeIsTheNormalisedValueAndNotTheRawPercentageUsed() =>
        Assert.Equal("70% left", Row(Attribute(202, value: 70, raw: 30)).Reading);

    [Fact]
    public void SpareBlocksAtAFixedZeroValueAreNotAFailure()
    {
        // The note fixes 180's value and worst at 0, with the always-passing threshold 00h.
        var row = Row(Attribute(180, value: 0, worst: 0, raw: 120, preFail: true, threshold: 0x00));

        Assert.Equal("120", row.Reading);
        Assert.Equal(AttributeStanding.Normal, row.Standing);
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(180, value: 0, worst: 0, raw: 120, preFail: true, threshold: 0x00), Outside).Standing);
    }

    [Fact]
    public void ThresholdFfAlwaysFailsOnACoveredDrive()
    {
        Assert.Equal(AttributeStanding.Failing, Row(Attribute(5, value: 100, preFail: true, threshold: 0xFF)).Standing);
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(5, value: 100, preFail: true, threshold: 0xFF), Outside).Standing);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFE)]
    public void ThresholdsThatAlwaysPassOrAreInvalidNeverFail(byte threshold) =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(5, value: 1, preFail: true, threshold: threshold)).Standing);

    [Fact]
    public void ThresholdFfDoesNotFailAnAdvisoryAttribute() =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(202, value: 100, preFail: false, threshold: 0xFF)).Standing);

    [Fact]
    public void WriteAmplificationIsWorkedOutFromTheTwoPageCounts()
    {
        var rows = Rows(Covered, null, Attribute(247, raw: 1000), Attribute(248, raw: 370));
        var amplification = Assert.Single(rows, row => row.Key == "write-amplification");

        Assert.Equal(1.37.ToString("0.00", null), amplification.Reading);
        Assert.Equal(AttributeStanding.Normal, amplification.Standing);
    }

    [Fact]
    public void WriteAmplificationIsNotStatedOutsideTheKey() =>
        Assert.Equal(2, Rows(Outside, null, Attribute(247, raw: 1000), Attribute(248, raw: 370)).Count);

    [Fact]
    public void WriteAmplificationNeedsBothCountsAndSomeHostWrites()
    {
        Assert.Single(Rows(Covered, null, Attribute(247, raw: 1000)));
        Assert.Single(Rows(Covered, null, Attribute(248, raw: 370)));
        Assert.Equal(2, Rows(Covered, null, Attribute(247, raw: 0), Attribute(248, raw: 370)).Count);
    }
}
