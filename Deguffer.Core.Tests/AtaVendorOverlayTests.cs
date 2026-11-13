using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: a vendor's reading applies only to the model families its own document covers. Each key has
/// a model it covers and one just outside it, which must stay as the catalogue reports it. Every model
/// name is invented.
/// </summary>
public sealed class AtaVendorOverlayTests
{
    private const string Read = "50% left";
    private const string AsReported = "Scored 50";

    private const string Pm863 = "SAMSUNG MZ7LM999HCTE-00000";
    private const string Sm863 = "SAMSUNG MZ7KM999HATE-00000";
    private const string Dc845Evo = "Samsung SSD 845DC EVO 999GB";
    private const string Dc845Pro = "Samsung SSD 845 DC PRO 999GB";

    /// <summary>A PM863a part number, which shares the PM863's prefix and no document covers.</summary>
    private const string Pm863a = "SAMSUNG MZ7LM999HMTE-00000";

    /// <summary>A part number in the FireCuda 120's form: ZA, a capacity, then GM10001.</summary>
    private const string FireCuda120 = "ZA999GM10001";

    /// <summary>A Seagate SSD part number with another family code after the capacity.</summary>
    private const string FireCuda120Sibling = "ZA999CM10003";

    /// <summary>A Seagate hard drive's name. No published Seagate document gives their attribute layouts.</summary>
    private const string SeagateHardDrive = "ST9999TEST000-1TEST";

    /// <summary>A consumer model's name. No Samsung document covers the consumer drives.</summary>
    private const string Consumer = "Samsung SSD 999 EVO 1TB";

    private static string Reading(byte id, string model, string firmware = "TEST01", ulong raw = 0, byte value = 50) =>
        Row(id, model, firmware, raw, value).Reading;

    private static HealthAttribute Row(byte id, string model, string firmware = "TEST01", ulong raw = 0, byte value = 50) =>
        Assert.Single(AtaAttributeRows.Of([new AtaAttribute(id, false, value, value, raw, null)], AtaDrives.Named(model, firmware), logicalSectorBytes: null));

    [Theory]
    [InlineData("Micron_M600_MTFDTEST999", "MU01")]
    [InlineData("Micron_1100_MTFDTEST999", "M0TEST")]
    [InlineData("Micron_1300_MTFDTEST999", "M5TEST")]
    [InlineData("Crucial_CT999MX100SSD1", "MU01")]
    [InlineData("Crucial_CT999MX200SSD1", "MU01")]
    [InlineData("CT9999MX300SSD4", "M0TEST")]
    [InlineData("Micron_M500_MTFDTEST999", "MU03")]
    [InlineData("Crucial_CT999M500SSD1", "MU05")]
    [InlineData("MTFDDAK999MBF-1TEST", "MU01")]
    [InlineData("MTFDDAV9T9TBN", "M0TEST")]
    [InlineData("M550-MTFDDAT999MAY", "MU01")]
    [InlineData("MTFDDAK999TDL-1TEST", "M5TEST")]
    [InlineData("MTFDDAK999MAV", "MU03")]
    public void MicronLifeRemainingIsReadOnTheFamiliesItsNoteLists(string model, string firmware) =>
        Assert.Equal(Read, Reading(202, model, firmware));

    [Theory]
    [InlineData("CT999MX500SSD1", "M3TEST")]
    [InlineData("CT999BX500SSD1", "M6TEST")]
    [InlineData("Micron_M500DC_MTFDTEST999", "MU05")]
    [InlineData("Micron_M510DC_MTFDTEST999", "0TEST")]
    [InlineData("Micron_5100_MTFDTEST999", "D0TEST")]
    [InlineData("Micron_M500_MTFDTEST999", "MU02")]
    [InlineData("Crucial_CT999M500SSD1", "MU02")]
    [InlineData("Crucial_CT999M500SSD1", "KU01")]
    [InlineData(Pm863, "TEST01")]
    [InlineData("CT999", "TEST01")]
    [InlineData("MTFDDAK999MBB", "MU05")]
    [InlineData("MTFDDAK999MBP", "0TEST")]
    [InlineData("MTFDDAK999TCB", "D0TEST")]
    [InlineData("MTFDDAK999MAV", "MU02")]
    [InlineData("XMTFDDAK999MBF", "MU01")]
    public void MicronLifeRemainingIsNotReadOutsideThem(string model, string firmware) =>
        Assert.Equal(AsReported, Reading(202, model, firmware));

    [Theory]
    [InlineData("INTEL SSDSC2BB999G4")]
    [InlineData("INTEL SSDSC2BB099T4")]
    [InlineData("INTEL SSDSC1NB999G401")]
    public void IntelMediaWearIsReadOnTheDcS3500(string model) =>
        Assert.Equal(Read, Reading(233, model));

    [Theory]
    [InlineData("INTEL SSDSC2BB999G6")]
    [InlineData("INTEL SSDSC2BB999G7")]
    [InlineData("INTEL SSDSC2BW999A4")]
    [InlineData("SOLIDIGM SSDSC2KB999G8")]
    public void IntelMediaWearIsNotReadOutsideIt(string model) =>
        Assert.Equal(AsReported, Reading(233, model));

    [Theory]
    [InlineData(Pm863, Read)]
    [InlineData(Sm863, Read)]
    [InlineData(Pm863a, AsReported)]
    [InlineData("SAMSUNG MZ7KM999HMTE-00000", AsReported)]
    [InlineData("SAMSUNG MZ7GE240TEST-00000", AsReported)]
    [InlineData("Samsung SSD 999 TEST 1TB", AsReported)]
    public void SamsungLifeRemainingIsReadOnlyOnThePm863AndSm863(string model, string expected) =>
        Assert.Equal(expected, Reading(177, model));

    [Theory]
    [InlineData(Pm863, true)]
    [InlineData(Sm863, true)]
    [InlineData(Dc845Evo, true)]
    [InlineData("Samsung SSD 845DC PRO 999GB", true)]
    [InlineData("SAMSUNG MZ7GE240TEST-00000", false)]
    [InlineData("SAMSUNG MZ7WD400TEST-00000", false)]
    [InlineData("Samsung SSD 845 TEST 999GB", false)]
    [InlineData(Pm863a, false)]
    [InlineData("Samsung SSD 999 TEST 1TB", false)]
    public void SamsungDataWrittenIsReadInSectorsOnlyWhereADocumentSaysSo(string model, bool covered) =>
        Assert.Equal(covered ? "1.0 MB" : AsReported, Reading(241, model, raw: 2048));

    [Fact]
    public void SamsungDataReadStaysAsReportedBecauseNoDocumentStatesItsUnit() =>
        Assert.Equal(AsReported, Reading(242, Pm863, raw: 2048));

    [Theory]
    [InlineData(179, Pm863)]
    [InlineData(181, Pm863)]
    [InlineData(182, Sm863)]
    [InlineData(183, Sm863)]
    public void SamsungAllowancesAreReadByTheirNormalisedValueOnThePm863AndSm863(byte id, string model) =>
        Assert.Equal(Read, Reading(id, model, raw: 7));

    [Theory]
    [InlineData(179, 10, AttributeStanding.Watch)]
    [InlineData(183, 10, AttributeStanding.Watch)]
    [InlineData(181, 11, AttributeStanding.Normal)]
    public void SamsungAllowancesWarnBeforeTheWhitePapersReplacementPoint(byte id, byte value, AttributeStanding expected) =>
        Assert.Equal(expected, Row(id, Pm863, value: value).Standing);

    [Theory]
    [InlineData(179, Pm863a)]
    [InlineData(181, Pm863a)]
    [InlineData(179, Consumer)]
    [InlineData(181, Consumer)]
    [InlineData(182, Consumer)]
    [InlineData(183, Consumer)]
    [InlineData(179, Dc845Evo)]
    [InlineData(179, Dc845Pro)]
    public void SamsungAllowancesAreNotReadWhereNoDocumentDefinesThem(byte id, string model) =>
        Assert.Equal(AsReported, Reading(id, model, raw: 7));

    [Theory]
    [InlineData(181, Dc845Evo)]
    [InlineData(182, Dc845Pro)]
    [InlineData(183, Dc845Evo)]
    public void Samsung845DcFailuresAreCountedFromTheRawValue(byte id, string model)
    {
        Assert.Equal(("None", AttributeStanding.Normal), Standing(Row(id, model, raw: 0)));
        Assert.Equal(("3", AttributeStanding.Watch), Standing(Row(id, model, raw: 3)));
    }

    [Theory]
    [InlineData(177, Dc845Evo)]
    [InlineData(180, Dc845Pro)]
    [InlineData(235, Dc845Evo)]
    [InlineData(250, Dc845Pro)]
    public void Samsung845DcCountsAreReadFromTheRawValue(byte id, string model) =>
        Assert.Equal(("7", AttributeStanding.Normal), Standing(Row(id, model, raw: 7)));

    [Theory]
    [InlineData(180, Pm863)]
    [InlineData(235, Pm863)]
    [InlineData(250, Sm863)]
    [InlineData(177, Consumer)]
    [InlineData(180, Consumer)]
    [InlineData(235, Consumer)]
    [InlineData(250, Consumer)]
    public void Samsung845DcCountsAreNotReadOnOtherFamilies(byte id, string model) =>
        Assert.Equal(AsReported, Reading(id, model, raw: 7));

    [Theory]
    [InlineData(0x00UL, "Normal", AttributeStanding.Normal)]
    [InlineData(0x10UL, "Capacitor failed", AttributeStanding.Failing)]
    [InlineData(0x20UL, "Read-only", AttributeStanding.Failing)]
    [InlineData(0x30UL, "Code 0x30", AttributeStanding.Normal)]
    public void Samsung845DcModeStatusIsReadFromItsDocumentedCodes(ulong raw, string reading, AttributeStanding standing)
    {
        var row = Row(202, Dc845Pro, raw: raw);

        Assert.Equal("Drive mode", row.Name);
        Assert.Equal((reading, standing), Standing(row));
    }

    [Theory]
    [InlineData(Pm863)]
    [InlineData(Consumer)]
    public void SamsungModeStatusIsNotReadWhereNoDocumentDefinesIt(string model) =>
        Assert.Equal(AsReported, Reading(202, model, raw: 0x20));

    /// <summary>
    /// The life left is in the first raw byte, under a throttling level, while the normalised value stays
    /// at the 64h the manual lists, so a reading of either the normalised value or the whole raw value fails.
    /// </summary>
    [Theory]
    [InlineData(FireCuda120, "7% left")]
    [InlineData("Seagate FireCuda 120 SSD ZA9999GM10001", "7% left")]
    [InlineData(FireCuda120Sibling, "Scored 100")]
    [InlineData("ZA999GM10002", "Scored 100")]
    [InlineData("XZA999GM10001", "Scored 100")]
    [InlineData(SeagateHardDrive, "Scored 100")]
    public void SeagateLifeRemainingIsReadFromTheFirstRawByteOnlyOnTheFireCuda120(string model, string expected) =>
        Assert.Equal(expected, Reading(231, model, raw: ThrottledLifeLeft(7), value: 100));

    [Fact]
    public void FireCuda120LifeRemainingWarnsNearTheEndOfLife()
    {
        Assert.Equal(AttributeStanding.Watch, Row(231, FireCuda120, raw: ThrottledLifeLeft(10), value: 100).Standing);
        Assert.Equal(AttributeStanding.Normal, Row(231, FireCuda120, raw: ThrottledLifeLeft(11), value: 100).Standing);
    }

    [Fact]
    public void FireCuda120SpareBlocksWarnNearTheEndOfLife()
    {
        Assert.Equal(AttributeStanding.Watch, Row(170, FireCuda120, value: 10).Standing);
        Assert.Equal(AttributeStanding.Normal, Row(170, FireCuda120, value: 11).Standing);
    }

    [Fact]
    public void FireCuda120SpareBlocksAreReadByTheirNormalisedValue() =>
        Assert.Equal(Read, Reading(170, FireCuda120, raw: 7));

    /// <summary>The figure is in the first raw byte, so the bytes above it, and the normalised value the manual fixes at 0, are not read.</summary>
    [Fact]
    public void FireCuda120WearRangeIsAPercentageInTheFirstRawByte()
    {
        var row = Assert.Single(AtaAttributeRows.Of([new AtaAttribute(177, false, 0, 0, 0x0000_0102_0007UL, null)], AtaDrives.Named(FireCuda120, "TEST01"), logicalSectorBytes: null));

        Assert.Equal(("Wear range", "7%", AttributeStanding.Normal), (row.Name, row.Reading, row.Standing));
    }

    [Theory]
    [InlineData(233, "Total written to flash")]
    [InlineData(241, "Total data written")]
    [InlineData(242, "Total data read")]
    public void FireCuda120DataTotalsAreGigabytesInTheManualsOwnUnit(byte id, string name)
    {
        var row = Row(id, FireCuda120, raw: 0x0100_0000_3039UL);

        Assert.Equal((name, "12,345 GB", AttributeStanding.Normal), (row.Name, row.Reading, row.Standing));
        Assert.Contains("does not say whether a gigabyte is a thousand or 1,024 megabytes", row.Meaning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(170, FireCuda120Sibling)]
    [InlineData(177, FireCuda120Sibling)]
    [InlineData(233, FireCuda120Sibling)]
    [InlineData(241, SeagateHardDrive)]
    [InlineData(242, SeagateHardDrive)]
    [InlineData(177, Consumer)]
    public void FireCuda120ReadingsAreNotTakenForOtherDrives(byte id, string model) =>
        Assert.Equal(AsReported, Reading(id, model, raw: 7));

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(173)]
    [InlineData(232)]
    [InlineData(234)]
    [InlineData(235)]
    public void FireCuda120AttributesWithoutAStatedLayoutStayAsTheCatalogueReportsThem(byte id) =>
        Assert.Equal(Row(id, SeagateHardDrive, raw: 7), Row(id, FireCuda120, raw: 7));

    /// <summary>A FireCuda 120 attribute 231 raw value: the life left in byte 0 and a throttling level of 2 in byte 4.</summary>
    private static ulong ThrottledLifeLeft(byte percent) => 0x0002_0000_0000UL | percent;

    private static (string Reading, AttributeStanding Standing) Standing(HealthAttribute row) => (row.Reading, row.Standing);
}
