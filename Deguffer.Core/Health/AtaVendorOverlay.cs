using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// What a manufacturer's own document states more exactly than the catalogue can for every drive: an
/// attribute's meaning, a threshold code, or a figure worked out from two attributes. Each is keyed on
/// the model families its document names and no wider, because the same ID means something else on
/// another family, even from the same maker. A family no document covers keeps the catalogue's
/// reading and the plain threshold rule (§7.6).
/// </summary>
internal static partial class AtaVendorOverlay
{
    /// <summary>The threshold code TN-FD-22 table 7 defines as always failing.</summary>
    private const byte AlwaysFailingThreshold = 0xFF;

    private static readonly AtaAttributeDefinition RemainingLife = new(
        "Remaining life", "How much of the drive's rated write life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition MediaWearout = new(
        "Media wear indicator", "How much of the flash memory's rated life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition TotalWritten = new(
        "Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.Bytes512, AttributeRole.Usage);

    private static readonly AtaAttributeDefinition SpareBlocksLeft = new(
        "Spare blocks left", "How much of the drive's reserve of spare blocks, which stand in for failed ones, is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition ProgramFailureAllowance = new(
        "Write failure allowance", "How much of the drive's allowance for failed writes to flash memory is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition EraseFailureAllowance = new(
        "Erase failure allowance", "How much of the drive's allowance for failed erases of flash memory is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition BadBlockAllowance = new(
        "Bad block allowance", "How much of the drive's allowance for blocks that fail in use is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition AverageErases = new(
        "Wear levelling count", "How many times, on average, each block of flash memory has been erased.", SmartRawFormat.Count, AttributeRole.Usage);

    private static readonly AtaAttributeDefinition SpareBlocksUnused = new(
        "Spare blocks left", "How many spare blocks the drive still has to stand in for failed ones.", SmartRawFormat.Count, AttributeRole.Information);

    private static readonly AtaAttributeDefinition ProgramFailures = new(
        "Write failures", "Writes to flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount);

    private static readonly AtaAttributeDefinition EraseFailures = new(
        "Erase failures", "Erases of flash memory that failed.", SmartRawFormat.Count, AttributeRole.FailureCount);

    private static readonly AtaAttributeDefinition RuntimeBadBlocks = new(
        "Failures in use", "Reads, writes and erases of flash memory that failed, all together.", SmartRawFormat.Count, AttributeRole.FailureCount);

    private static readonly AtaAttributeDefinition SuddenPowerLoss = new(
        "Unexpected power loss", "Times power was lost without warning, so the drive had to recover its data when next switched on.", SmartRawFormat.Count, AttributeRole.Usage);

    private static readonly AtaAttributeDefinition InterfaceDownshifts = new(
        "Interface speed drops", "Times the connection to the computer settled at a slower speed than it had before.", SmartRawFormat.Count, AttributeRole.Information);

    private static readonly AtaStatusDefinition SsdModeStatus = new(
        "Drive mode",
        "The state the drive reports itself to be in.",
        new Dictionary<ulong, AtaStatusCode>
        {
            [0x00] = new("Normal", "The drive reports that it is working normally.", AttributeStanding.Normal),
            [0x10] = new(
                "Capacitor failed",
                "The capacitor that protects data in a power cut has failed, so a power cut can lose data being written. Copy your important files somewhere else, and plan to replace the drive.",
                AttributeStanding.Failing),
            [0x20] = new(
                "Read-only",
                "The drive has switched itself to read-only, so nothing more can be written to it. Copy your important files somewhere else now.",
                AttributeStanding.Failing),
        }.ToFrozenDictionary());

    /// <summary>The overlay's definition for <paramref name="id"/> on <paramref name="drive"/>, if a document covers it.</summary>
    internal static AtaAttributeDefinition? Of(byte id, DeviceDescriptor drive) =>
        MicronClientSata.Covers(drive) ? MicronClientSata.Of(id)
        : SamsungPm863OrSm863().IsMatch(drive.Model) ? SamsungPm863OrSm863Of(id)
        : Samsung845Dc().IsMatch(drive.Model) ? Samsung845DcOf(id)
        : id switch
        {
            // Intel DC S3500 product specification 328860-008US, table 18: the normalised value declines
            // from 100 to 1 with wear. Solidigm KA-00033 names 233 for later drives but states no scale.
            233 when IntelDcS3500().IsMatch(drive.Model) => MediaWearout,

            _ => null,
        };

    /// <summary>
    /// Whether <paramref name="threshold"/> fails its attribute whatever the value. TN-FD-22 table 7 makes
    /// FFh a valid threshold that always fails. Elsewhere FEh and FFh are left unjudged, as no document says
    /// otherwise. 00h always passes and FEh is invalid on every drive, which the plain rule already gives.
    /// </summary>
    internal static bool AlwaysFails(byte threshold, DeviceDescriptor drive) =>
        threshold == AlwaysFailingThreshold && MicronClientSata.Covers(drive);

    /// <summary>
    /// The attributes counting flash pages written for the computer and by the drive itself, from which
    /// the write amplification is (host + drive) / host, where a document defines them (TN-FD-22, ID 248).
    /// </summary>
    internal static (byte Host, byte Drive)? FlashPagesWritten(DeviceDescriptor drive) =>
        MicronClientSata.Covers(drive) ? (MicronClientSata.HostPagesId, MicronClientSata.DrivePagesId) : null;

    /// <summary>
    /// Samsung, "Using SMART Attributes to Estimate Drive Lifetime" (WHP-SSD-SSDSMARTATTRIBUTES-APR16J),
    /// which covers the PM863 and SM863. For 177, 179, 181, 182 and 183 the normalised value is the
    /// percentage left. The paper advises replacing the drive once 179, 181, 182 or 183 falls below 10,
    /// which the general warning at 10 or less already gives. The 845 DC note counts used reserve
    /// blocks in 179's raw value where this paper counts unused ones, so 179 is read by its normalised
    /// value only. The paper states no unit for 242, so 242 stays as reported. Its timed workload
    /// attributes, 245 to 247, describe a measurement that Samsung's tool starts with a vendor command,
    /// which Deguffer never sends (§7.6).
    /// </summary>
    private static AtaAttributeDefinition? SamsungPm863OrSm863Of(byte id) => id switch
    {
        177 => RemainingLife,
        179 => SpareBlocksLeft,
        181 => ProgramFailureAllowance,
        182 => EraseFailureAllowance,
        183 => BadBlockAllowance,
        241 => TotalWritten,
        _ => null,
    };

    /// <summary>
    /// Samsung's S.M.A.R.T. application note for the 845 DC PRO and 845 DC EVO (2014), which defines
    /// the raw values and no normalised scale. 179 is left out because the note and the PM863 paper
    /// disagree on whether it counts used or unused reserve blocks. The note states no unit for 242,
    /// so 242 stays as reported. Its other IDs are the common ones the catalogue already reads.
    /// </summary>
    private static AtaAttributeDefinition? Samsung845DcOf(byte id) => id switch
    {
        177 => AverageErases,
        180 => SpareBlocksUnused,
        181 => ProgramFailures,
        182 => EraseFailures,
        183 => RuntimeBadBlocks,
        202 => SsdModeStatus,
        235 => SuddenPowerLoss,
        241 => TotalWritten,
        250 => InterfaceDownshifts,
        _ => null,
    };

    /// <summary>
    /// The PM863 (MZ7LM...HC) and SM863 (MZ7KM...HA) part numbers in Samsung's data sheets. The PM863a
    /// and SM863a share the prefixes with other letters after the capacity, and are not covered.
    /// </summary>
    [GeneratedRegex(@"\bMZ7(?:LM[0-9T]{3}HC|KM[0-9T]{3}HA)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SamsungPm863OrSm863();

    /// <summary>
    /// The 845 DC EVO and 845 DC PRO by product name. Their data sheets give retail order codes and no
    /// part numbers, so no part number can be tied to either family.
    /// </summary>
    [GeneratedRegex(@"\bSamsung SSD 845 ?DC (?:EVO|PRO)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Samsung845Dc();

    /// <summary>
    /// The DC S3500 part numbers in Intel's discontinuance notices: SSDSC2BB (2.5-inch) or SSDSC1NB
    /// (1.8-inch), three capacity digits, then G4 or T4. The S3510 and S3520 end G6/T6 and G7/T7.
    /// </summary>
    [GeneratedRegex(@"\bSSDSC(?:2BB|1NB)\d{3}[GT]4", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntelDcS3500();
}
