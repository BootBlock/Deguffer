using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// Intel's product specifications for the DC S3500, DC S3510 and DC S3520: the family each covers, and
/// what each attribute means on it where the catalogue cannot say. The IDs the catalogue already reads
/// as the tables state (05h, 09h, 0Ch, AEh, B8h, BBh, C2h, C5h, C7h) are left to it. Solidigm KA-00033
/// names the same IDs for later Intel and Solidigm SATA drives without stating every unit, and no
/// document covers Intel's client drives, so neither is covered.
/// </summary>
internal static partial class IntelDcSata
{
    /// <summary>The tables define no other figure in these raw values, so each count fills all six bytes.</summary>
    private const RawWidth Whole = RawWidth.AllSixBytes;

    private const string WorkloadWearName = "Wear in the timed workload";

    private const string WorkloadWearExplanation =
        "How much of the flash memory's rated life the drive has used since its workload timer, ID 228, was last started.";

    private static readonly AtaAttributeDefinition SpareBlocksLeft = new(
        "Spare blocks left", "How much of the drive's reserve of spare blocks, which stand in for failed ones, is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    /// <summary>What all three tables state alike.</summary>
    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> Common = new Dictionary<byte, AtaAttributeDefinition>
    {
        // AAh is defined as E8h repeated.
        [170] = SpareBlocksLeft,
        [171] = new("Program failures", "Writes to flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount),
        [172] = new("Erase failures", "Erases of flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount),

        // Each table gives the threshold 10, which judges the normalised value. 328860 adds that the drive
        // sets that value to 1 when a test fails.
        [175] = new(
            "Power-loss capacitor test",
            "The drive's last test of the capacitor that keeps it running long enough to save data in a power cut.",
            SmartRawFormat.CapacitorTest,
            AttributeRole.Information),
        [183] = new("SATA speed drops", "Times the link to the computer fell to a slower SATA speed because of errors.", SmartRawFormat.Count, AttributeRole.Information),
        [192] = new("Unsafe shutdowns", "Times the drive was switched off without being told to stop first.", SmartRawFormat.Count, AttributeRole.Usage),
        [225] = new("Data written by the computer", "How much the computer has written to the drive over its life.", SmartRawFormat.Units32MiB, AttributeRole.Usage, Whole),

        // E2h to E4h measure from when the workload timer was last started, which SMART EXECUTE OFF-LINE
        // IMMEDIATE with subcommand 40h does. Deguffer never sends it (§7.6), and only reads the result.
        [227] = new(
            "Reads in the timed workload",
            "The share of the computer's requests that were reads since the drive's workload timer, ID 228, was last started.",
            SmartRawFormat.RawPercent,
            AttributeRole.Information),
        [228] = new(
            "Workload timer",
            "How long ago the drive's workload timer was last started. IDs 226 and 227 are measured over this time.",
            SmartRawFormat.Minutes,
            AttributeRole.Information),
        [232] = SpareBlocksLeft,

        // The normalised value falls from 100 to 1 as the average erase count reaches the rated maximum,
        // and stays at 1 after. 328860 gives the raw value as always 0.
        [233] = new("Media wear indicator", "How much of the flash memory's rated life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife),
        [234] = new(
            "Thermal throttling",
            "Whether the drive is slowing itself down to cool off now, and how many times it has done so over its life.",
            SmartRawFormat.ThrottleStatus,
            AttributeRole.Information),

        // F1h counts the same host writes as E1h.
        [241] = new("Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.Units32MiB, AttributeRole.Usage, Whole),
        [242] = new("Total data read", "How much the computer has read from the drive over its life.", SmartRawFormat.Units32MiB, AttributeRole.Usage, Whole),
    }.ToFrozenDictionary();

    /// <summary>
    /// Intel SSD DC S3500 Series product specification 328860-008US (January 2015), table 18 and section
    /// 5.4.2. Of the three tables, only this one packs a temperature range and an overheat count into BEh,
    /// and it works E2h to three decimal places.
    /// </summary>
    private static readonly AtaAttributeDocument DcS3500 = new(DcS3500Models(), With(new()
    {
        [190] = new(
            "Case temperature",
            "The temperature of the drive's case, with the lowest and highest it has recorded recently, and the times it has run hotter than it is rated for.",
            SmartRawFormat.CelsiusWithOverheats,
            AttributeRole.Temperature),
        [226] = new(WorkloadWearName, WorkloadWearExplanation, SmartRawFormat.PercentPer1024ToThousandths, AttributeRole.Information),
    }));

    /// <summary>
    /// The table that 332211-004US, 334826-001US and 334827-001US give word for word. Against 328860 it works E2h to two
    /// decimal places, a change 332211's revision 002 records, and it adds F3h: everything written to the
    /// flash memory, in units of 65,536 sectors filling all six bytes. It names BEh the case temperature
    /// with the same value as C2h, and also gives its raw value as 100 minus that temperature. Those two
    /// layouts conflict, so BEh keeps the catalogue's reading.
    /// </summary>
    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> DcS3510AndS3520Attributes = With(new()
    {
        [226] = new(WorkloadWearName, WorkloadWearExplanation, SmartRawFormat.PercentPer1024ToHundredths, AttributeRole.Information),
        [243] = new(
            "Total written to flash",
            "How much the drive has written to its flash memory over its life: what the computer sent and the drive's own upkeep together.",
            SmartRawFormat.Units32MiB,
            AttributeRole.Usage,
            Whole),
    });

    /// <summary>Intel SSD DC S3510 Series product specification 332211-004US (February 2016), table 15 and section 5.4.2.</summary>
    private static readonly AtaAttributeDocument DcS3510 = new(DcS3510Models(), DcS3510AndS3520Attributes);

    /// <summary>
    /// Intel SSD DC S3520 Series product specification 334826-001US (August 2016), table 24 and section
    /// 5.4.2, which covers the 2.5-inch and M.2 drives. It leaves the 2.5-inch 150GB drive to its own
    /// specification, 334827-001US (August 2016), whose table 15 and section 5.4.2 are the same.
    /// </summary>
    private static readonly AtaAttributeDocument DcS3520 = new(DcS3520Models(), DcS3510AndS3520Attributes);

    private static readonly AtaAttributeDocument[] Documents = [DcS3500, DcS3510, DcS3520];

    /// <summary>
    /// The meaning that the specification covering <paramref name="drive"/> gives <paramref name="id"/>, or
    /// null where no specification covers the drive or the catalogue's reading stands.
    /// </summary>
    internal static AtaAttributeDefinition? Of(byte id, DeviceDescriptor drive) => AtaAttributeDocument.Of(Documents, id, drive);

    /// <summary>A family's table: the attributes all three state alike, and <paramref name="own"/>, which no two IDs share.</summary>
    private static FrozenDictionary<byte, AtaAttributeDefinition> With(Dictionary<byte, AtaAttributeDefinition> own)
    {
        foreach (var (id, definition) in Common)
        {
            own.Add(id, definition);
        }

        return own.ToFrozenDictionary();
    }

    /// <summary>
    /// The DC S3500 part numbers in Intel's discontinuance notices: SSDSC2BB (2.5-inch) or SSDSC1NB
    /// (1.8-inch), three capacity digits, then G4 or T4. A letter straight after marks a system maker's
    /// own firmware, such as Dell's R, whose layout no Intel document states.
    /// </summary>
    [GeneratedRegex(@"\bSSDSC(?:2BB|1NB)\d{3}[GT]4(?![A-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DcS3500Models();

    /// <summary>
    /// The DC S3510 part numbers in discontinuance notice 114906: SSDSC2BB, then 080G, 120G, 240G, 480G,
    /// 800G, 012T or 016T, then 6. The specification covers only the 2.5-inch drive. A letter straight
    /// after marks a system maker's own firmware, as on the DC S3500.
    /// </summary>
    [GeneratedRegex(@"\bSSDSC2BB(?:080G|120G|240G|480G|800G|012T|016T)6(?![A-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DcS3510Models();

    /// <summary>
    /// The DC S3520 part numbers: SSDSC2BB (2.5-inch) with the capacities discontinuance notice 115910
    /// lists, or SSDSCKJB (M.2) with those 334826 names for it, then 7. Dell's own firmware,
    /// SSDSC2BB...G7R, reports a different layout altogether, so a letter straight after is left out. The
    /// DC S3320, which 334826 does not cover, shares the 2.5-inch ordering codes with O1 after them, and
    /// a model name that drops that suffix cannot be told apart.
    /// </summary>
    [GeneratedRegex(
        @"\bSSDSC(?:2BB(?:150G|240G|480G|800G|960G|012T|016T)|KJB(?:150G|240G|480G|760G|960G))7(?![A-Z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DcS3520Models();
}
