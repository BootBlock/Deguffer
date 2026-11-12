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

    private static string Reading(byte id, string model, string firmware = "TEST01", ulong raw = 0) =>
        Assert.Single(AtaAttributeRows.Of([new AtaAttribute(id, false, 50, 50, raw, null)], AtaDrives.Named(model, firmware))).Reading;

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
    [InlineData("SAMSUNG MZ7LM999HCTE-00000", "TEST01")]
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
    [InlineData("SAMSUNG MZ7LM999HCTE-00000", Read)]
    [InlineData("SAMSUNG MZ7KM999HATE-00000", Read)]
    [InlineData("SAMSUNG MZ7LM999HMTE-00000", AsReported)]
    [InlineData("SAMSUNG MZ7KM999HMTE-00000", AsReported)]
    [InlineData("SAMSUNG MZ7GE240TEST-00000", AsReported)]
    [InlineData("Samsung SSD 999 TEST 1TB", AsReported)]
    public void SamsungLifeRemainingIsReadOnlyOnThePm863AndSm863(string model, string expected) =>
        Assert.Equal(expected, Reading(177, model));

    [Theory]
    [InlineData("SAMSUNG MZ7LM999HCTE-00000", true)]
    [InlineData("SAMSUNG MZ7KM999HATE-00000", true)]
    [InlineData("Samsung SSD 845DC EVO 999GB", true)]
    [InlineData("Samsung SSD 845DC PRO 999GB", true)]
    [InlineData("SAMSUNG MZ7GE240TEST-00000", false)]
    [InlineData("SAMSUNG MZ7WD400TEST-00000", false)]
    [InlineData("Samsung SSD 845 TEST 999GB", false)]
    [InlineData("SAMSUNG MZ7LM999HMTE-00000", false)]
    [InlineData("Samsung SSD 999 TEST 1TB", false)]
    public void SamsungDataWrittenIsReadInSectorsOnlyWhereADocumentSaysSo(string model, bool covered) =>
        Assert.Equal(covered ? "1.0 MB" : AsReported, Reading(241, model, raw: 2048));

    [Fact]
    public void SamsungDataReadStaysAsReportedBecauseNoDocumentStatesItsUnit() =>
        Assert.Equal(AsReported, Reading(242, "SAMSUNG MZ7LM999HCTE-00000", raw: 2048));
}
