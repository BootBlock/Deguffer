using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// Micron technical note TN-FD-22 Rev. E, "Client SATA SSD SMART Attribute Reference": the families it
/// covers, and what each attribute in its table 1 means on them. Every raw value the note defines is
/// 48 bits wide, so each count fills all six bytes.
/// </summary>
internal static partial class MicronClientSata
{
    /// <summary>The note's attribute 247: flash pages written to store what the computer sent.</summary>
    internal const byte HostPagesId = 247;

    /// <summary>The note's attribute 248: flash pages the drive wrote on top of those.</summary>
    internal const byte DrivePagesId = 248;

    /// <summary>The first M500 firmware revision the note covers, MU03.</summary>
    private const int FirstCoveredM500Firmware = 3;

    private const RawWidth Whole = RawWidth.AllSixBytes;

    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> Attributes = new Dictionary<byte, AtaAttributeDefinition>
    {
        [1] = new("Read error events", "Errors found while reading flash memory, corrected or not. Flash memory corrects some errors as a matter of course.", SmartRawFormat.Count, AttributeRole.Information, Whole),
        [5] = new("Retired flash blocks", "Blocks of flash memory that failed in use and that the drive has replaced.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [9] = new("Time powered on", "How long the drive has been switched on over its life. Time in its low-power states may not be counted.", SmartRawFormat.Hours, AttributeRole.Usage, Whole),
        [12] = new("Power cycles", "How many times the drive has been switched on.", SmartRawFormat.Count, AttributeRole.Usage, Whole),
        [171] = new("Program failures", "Writes to flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [172] = new("Erase failures", "Erases of flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [173] = new("Average erase count", "How many times each block of flash memory has been erased, on average. Each erase wears it a little.", SmartRawFormat.Count, AttributeRole.Usage, Whole),
        [174] = new("Unexpected power loss", "Times power was lost without the drive being told to stop first.", SmartRawFormat.Count, AttributeRole.Usage, Whole),

        // The note fixes the normalised and worst values at 0, so they say nothing. The raw value is the count.
        [180] = new("Spare blocks left", "Spare flash blocks the drive has left to replace failed ones. When none are left, the drive stops accepting writes.", SmartRawFormat.Count, AttributeRole.Information, Whole),
        [183] = new("SATA speed drops", "Times the link to the computer fell to a slower SATA speed than it had agreed.", SmartRawFormat.Count, AttributeRole.Information, Whole),
        [184] = new("Data path errors", "Errors found on the drive's internal path for data being written or read, corrected or not.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [187] = new("Reported uncorrectable errors", "Reads and other commands the drive could not correct and reported as failed.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [194] = new("Temperature", "The temperature the drive measures inside itself, with the lowest and highest it has recorded.", SmartRawFormat.CelsiusRange, AttributeRole.Temperature),
        [196] = new("Blocks failed in use", "Blocks of flash memory that have gone bad since the drive left the factory.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [197] = new("Pending read errors", "Errors found during the computer's reads or the drive's own background checks.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [198] = new("Errors in the last offline scan", "Errors the drive could not correct during its last offline scan. The count starts again with each scan.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [199] = new("Interface errors", "Corrupted transfers on the cable. Usually a loose or faulty cable, not the drive.", SmartRawFormat.Count, AttributeRole.Information, Whole),

        // The raw value is the percentage used, and the normalised value is 100 less that, so the page reads the latter.
        [202] = new("Remaining life", "How much of the drive's rated write life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife),
        [206] = new("Write failures", "Writes to flash memory that failed, which the drive's write error rate is worked out from.", SmartRawFormat.Count, AttributeRole.FailureCount, Whole),
        [210] = new("Pages rebuilt from parity", "Pages of flash memory the drive could not read directly and rebuilt from the redundant data it keeps.", SmartRawFormat.Count, AttributeRole.Information, Whole),

        // Micron counts the computer's writes here in logical sectors, and not in 241 as many makers do.
        [246] = new("Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.LogicalSectors, AttributeRole.Usage, Whole),
        [HostPagesId] = new("Flash pages written for the computer", "Pages of flash memory the drive has written to store what the computer sent.", SmartRawFormat.Count, AttributeRole.Usage, Whole),
        [DrivePagesId] = new("Flash pages written by the drive", "Pages of flash memory the drive has written for its own upkeep, on top of what the computer sent.", SmartRawFormat.Count, AttributeRole.Usage, Whole),
    }.ToFrozenDictionary();

    /// <summary>
    /// Whether the note covers <paramref name="drive"/>: one of the families it lists, under Micron's name,
    /// Micron's part number or Crucial's part number. The M500 is covered only from firmware MU03.
    /// </summary>
    internal static bool Covers(DeviceDescriptor drive) =>
        Family().Match(drive.Model) is { Success: true } match
        && (!IsM500(match) || IsM500FirmwareCovered(drive.Revision));

    /// <summary>The note's meaning for <paramref name="id"/>, or null where its table has no such attribute.</summary>
    internal static AtaAttributeDefinition? Of(byte id) => Attributes.GetValueOrDefault(id);

    private static bool IsM500(Match match) =>
        match.Groups["family"].ValueSpan.Equals("M500", StringComparison.OrdinalIgnoreCase)
        || match.Groups["code"].ValueSpan.Equals("MAV", StringComparison.OrdinalIgnoreCase);

    private static bool IsM500FirmwareCovered(string revision) =>
        revision.StartsWith("MU", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(revision.AsSpan(2), out var release)
        && release >= FirstCoveredM500Firmware;

    /// <summary>
    /// "Micron_M600_...", "Crucial_CT250MX200SSD1", or a Micron part number such as "MTFDDAK256MBF".
    /// The family must stand alone, so a data-centre sibling such as the M500DC, or the MX500 the note
    /// does not list, is not taken for one it does. In a part number the family is the code after the
    /// capacity, which Micron's data sheets give as MAV (M500), MAZ (M510), MAY (M550), MBF (M600),
    /// TBN (1100) and TDL (1300). The data-centre codes, such as MBB (M500DC), differ.
    /// </summary>
    [GeneratedRegex(
        @"^Micron[_ ](?<family>M500|M510|M550|M600|1100|1300)(?:[_ ]|$)|^(?:Crucial_)?CT\d{3,4}(?<family>M500|M550|MX100|MX200|MX300)SSD\d|(?<![A-Z0-9])MTFDD(?:AK|AT|AV|AY)(?:\d{3}|\dT\d)(?<code>MAV|MAZ|MAY|MBF|TBN|TDL)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Family();
}
