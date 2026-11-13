using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// Intel SSD DC S3500 Series product specification 328860-008US (January 2015), table 18 and section
/// 5.4.2: the family it covers, and what each attribute means on it where the catalogue cannot say.
/// The IDs the catalogue already reads as the table states (05h, 09h, 0Ch, AEh, B8h, BBh, C2h, C5h, C7h)
/// are left to it. Solidigm KA-00033 names the same IDs for later Intel and Solidigm SATA drives
/// without stating every unit, and no document covers Intel's client drives, so neither is covered.
/// </summary>
internal static partial class IntelDcS3500
{
    /// <summary>The table defines no other figure in these raw values, so each count fills all six bytes.</summary>
    private const RawWidth Whole = RawWidth.AllSixBytes;

    private static readonly AtaAttributeDefinition SpareBlocksLeft = new(
        "Spare blocks left", "How much of the drive's reserve of spare blocks, which stand in for failed ones, is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> Attributes = new Dictionary<byte, AtaAttributeDefinition>
    {
        // AAh is defined as E8h repeated.
        [170] = SpareBlocksLeft,
        [171] = new("Program failures", "Writes to flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount),
        [172] = new("Erase failures", "Erases of flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount),

        // The drive sets the normalised value to 1 when a test fails, which its threshold of 10 judges.
        [175] = new(
            "Power-loss capacitor test",
            "The drive's last test of the capacitor that keeps it running long enough to save data in a power cut.",
            SmartRawFormat.CapacitorTest,
            AttributeRole.Information),
        [183] = new("SATA speed drops", "Times the link to the computer fell to a slower SATA speed because of errors.", SmartRawFormat.Count, AttributeRole.Information),
        [190] = new(
            "Case temperature",
            "The temperature of the drive's case, with the lowest and highest it has recorded recently, and the times it has run hotter than it is rated for.",
            SmartRawFormat.CelsiusWithOverheats,
            AttributeRole.Temperature),
        [192] = new("Unsafe shutdowns", "Times the drive was switched off without being told to stop first.", SmartRawFormat.Count, AttributeRole.Usage),
        [225] = new("Data written by the computer", "How much the computer has written to the drive over its life.", SmartRawFormat.Units32MiB, AttributeRole.Usage, Whole),

        // E2h to E4h measure from when the workload timer was last started, which SMART EXECUTE OFF-LINE
        // IMMEDIATE with subcommand 40h does. Deguffer never sends it (§7.6), and only reads the result.
        [226] = new(
            "Wear in the timed workload",
            "How much of the flash memory's rated life the drive has used since its workload timer, ID 228, was last started.",
            SmartRawFormat.PercentPer1024,
            AttributeRole.Information),
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

        // The raw value is always 0. The normalised value falls from 100 to 1 as the average erase count
        // reaches the rated maximum, and stays at 1 after.
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

    internal static bool Covers(DeviceDescriptor drive) => Family().IsMatch(drive.Model);

    /// <summary>The specification's meaning for <paramref name="id"/>, or null where the catalogue's reading stands.</summary>
    internal static AtaAttributeDefinition? Of(byte id) => Attributes.GetValueOrDefault(id);

    /// <summary>
    /// The DC S3500 part numbers in Intel's discontinuance notices: SSDSC2BB (2.5-inch) or SSDSC1NB
    /// (1.8-inch), three capacity digits, then G4 or T4. The S3510 and S3520 end G6/T6 and G7/T7, and
    /// have specifications of their own, which this one does not speak for.
    /// </summary>
    [GeneratedRegex(@"\bSSDSC(?:2BB|1NB)\d{3}[GT]4", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Family();
}
