using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: Intel's DC S3510 specification, 332211-004US table 15, is read on the DC S3510, and its DC S3520
/// specification, 334826-001US table 24, on the 2.5-inch and M.2 DC S3520. The two tables are the same,
/// so each reading is checked on every family. Drives just outside each key keep the catalogue's
/// reading. The model names are Intel part numbers, one capacity from each part of a key, and every byte
/// is invented.
/// </summary>
public sealed class IntelDcS3510S3520Tests
{
    private const string DcS3510 = "INTEL SSDSC2BB480G6";
    private const string DcS3510Terabytes = "INTEL SSDSC2BB016T6";
    private const string DcS3520 = "INTEL SSDSC2BB960G7";
    private const string DcS3520Terabytes = "INTEL SSDSC2BB012T7";
    private const string DcS3520M2 = "INTEL SSDSCKJB760G7";

    private static HealthAttribute Row(byte id, string model, ulong raw = 0, byte value = 100) =>
        Assert.Single(AtaAttributeRows.Of([new AtaAttribute(id, false, value, value, raw, null)], AtaDrives.Named(model), logicalSectorBytes: null));

    private static string Reading(byte id, ulong raw, string model) => Row(id, model, raw).Reading;

    public static TheoryData<string> Covered => [DcS3510, DcS3510Terabytes, DcS3520, DcS3520Terabytes, DcS3520M2];

    public static TheoryData<byte, string, string> EveryNamedAttribute()
    {
        (byte Id, string Name)[] attributes =
        [
            (170, "Spare blocks left"),
            (171, "Program failures"),
            (172, "Erase failures"),
            (175, "Power-loss capacitor test"),
            (183, "SATA speed drops"),
            (192, "Unsafe shutdowns"),
            (225, "Data written by the computer"),
            (226, "Wear in the timed workload"),
            (227, "Reads in the timed workload"),
            (228, "Workload timer"),
            (232, "Spare blocks left"),
            (233, "Media wear indicator"),
            (234, "Thermal throttling"),
            (241, "Total data written"),
            (242, "Total data read"),
            (243, "Total written to flash"),
        ];

        var data = new TheoryData<byte, string, string>();
        foreach (var model in Covered)
        {
            foreach (var (id, name) in attributes)
            {
                data.Add(id, name, model);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryNamedAttribute))]
    public void EveryAttributeTheSpecificationDefinesIsNamed(byte id, string name, string model) =>
        Assert.Equal(name, Row(id, model).Name);

    /// <summary>
    /// A maker's firmware letter, a capacity the documents do not list for that form factor, the
    /// DC S3320's ordering code and the E 7000s, which shares the generation.
    /// </summary>
    [Theory]
    [InlineData("INTEL SSDSC2BB480G6R")]
    [InlineData("SSDSC2BB240G7R")]
    [InlineData("INTEL SSDSC2BB999G6")]
    [InlineData("INTEL SSDSC2BB760G7")]
    [InlineData("INTEL SSDSCKJB800G7")]
    [InlineData("INTEL SSDSC2BB150G7O1")]
    [InlineData("INTEL SSDSC2BR480G7")]
    public void AModelJustOutsideEachKeyKeepsTheCataloguesReading(string model)
    {
        Assert.Equal("Vendor-specific attribute 175", Row(175, model).Name);
        Assert.Equal("Load time", Row(226, model).Name);
        Assert.Equal("Vendor-specific attribute 243", Row(243, model).Name);
    }

    /// <summary>The table gives BEh two raw layouts that conflict, so the catalogue's reading stands.</summary>
    [Theory]
    [MemberData(nameof(Covered))]
    public void CaseTemperatureKeepsTheCataloguesReading(string model) =>
        Assert.Equal(("Airflow temperature", "35 °C"), (Row(190, model, 35).Name, Reading(190, 35, model)));

    /// <summary>332211 revision 002 cut E2h from three decimal places to two, and its example is 4455/1024 = 4.35%.</summary>
    [Theory]
    [MemberData(nameof(Covered))]
    public void TimedWorkloadWearIsTheRawValueOver1024ToTwoPlaces(string model)
    {
        Assert.Equal("0.00%", Reading(226, 0, model));
        Assert.Equal("4.35%", Reading(226, 4_455, model));
        Assert.Equal("4.34%", Reading(226, 4_450, model));
        Assert.Equal("0.01%", Reading(226, 16, model));
        Assert.Equal("100.00%", Reading(226, 102_400, model));
        Assert.Equal("Not measured yet", Reading(226, 0xFFFF, model));
    }

    [Theory]
    [MemberData(nameof(Covered))]
    public void FlashWritesAreCountedIn32MiBUnitsFillingAllSixBytes(string model)
    {
        Assert.Equal("32.0 MB", Reading(243, 1, model));
        Assert.Equal("1.0 TB", Reading(243, 32_768, model));
        Assert.Equal("128 PB", Reading(243, 1UL << 32, model));
        Assert.Equal(AttributeStanding.Normal, Row(243, model, 1UL << 32).Standing);
    }

    [Theory]
    [MemberData(nameof(Covered))]
    public void HostDataIsCountedIn32MiBUnits(string model)
    {
        Assert.Equal("1.0 GB", Reading(225, 32, model));
        Assert.Equal("1.0 GB", Reading(241, 32, model));
        Assert.Equal("1.0 GB", Reading(242, 32, model));
    }

    [Theory]
    [MemberData(nameof(Covered))]
    public void ACapacitorTestIsReadAsOnTheDcS3500(string model) =>
        Assert.Equal("Last test 1,200 µs, 30 minutes ago, 5 tests in all", Reading(175, 0x0005_001E_04B0UL, model));

    /// <summary>The table calls a status above 100 invalid, so it is not shown as a percentage.</summary>
    [Theory]
    [MemberData(nameof(Covered))]
    public void AThrottlingStatusAbove100IsNotAPercentage(string model)
    {
        Assert.Equal("Throttling 100%, 3 times in all", Reading(234, 0x0000_0000_0364UL, model));
        Assert.Equal("Unknown throttling status 101, 3 times in all", Reading(234, 0x0000_0000_0365UL, model));
    }
}
