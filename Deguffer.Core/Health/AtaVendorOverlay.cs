using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// The attributes a manufacturer's own document states more exactly than the catalogue can for every
/// drive. Each entry is keyed on the model families its document names and no wider, because the same
/// ID means something else on another family, even from the same maker. A family no document covers
/// keeps the catalogue's reading (§7.6).
/// </summary>
internal static partial class AtaVendorOverlay
{
    /// <summary>The first Micron M500 firmware revision TN-FD-22 covers, MU03.</summary>
    private const int FirstCoveredM500Firmware = 3;

    private static readonly AtaAttributeDefinition RemainingLife = new(
        "Remaining life", "How much of the drive's rated write life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition MediaWearout = new(
        "Media wear indicator", "How much of the flash memory's rated life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition TotalWritten = new(
        "Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.Bytes512, AttributeRole.Usage);

    /// <summary>The overlay's definition for <paramref name="id"/> on <paramref name="drive"/>, if a document covers it.</summary>
    internal static AtaAttributeDefinition? Of(byte id, DeviceDescriptor drive) => id switch
    {
        // Samsung, "Using SMART Attributes to Estimate Drive Lifetime" (WHP-SSD-SSDSMARTATTRIBUTES-APR16J):
        // the normalised value is the percentage of remaining P/E cycles, on the PM863 and SM863.
        177 when SamsungPm863OrSm863().IsMatch(drive.Model) => RemainingLife,

        // The same white paper, and Samsung's S.M.A.R.T. application note for the 845 DC PRO and EVO:
        // the raw value counts 512-byte sectors. Neither states a unit for 242, so 242 stays as reported.
        241 when SamsungPm863OrSm863().IsMatch(drive.Model) || Samsung845Dc().IsMatch(drive.Model) => TotalWritten,

        // Micron TN-FD-22 Rev. E, "Client SATA SSD SMART Attribute Reference": percent lifetime
        // remaining. Samsung uses 202 for a mode status, which the family names here exclude.
        202 when IsMicronClientSata(drive) => RemainingLife,

        // Intel DC S3500 product specification 328860-008US, table 18: the normalised value declines
        // from 100 to 1 with wear. Solidigm KA-00033 names 233 for later drives but states no scale.
        233 when IntelDcS3500().IsMatch(drive.Model) => MediaWearout,

        _ => null,
    };

    /// <summary>
    /// The families TN-FD-22 lists, under Micron's name, Micron's part number or Crucial's part number.
    /// The M500 is covered only from firmware MU03, as the note states.
    /// </summary>
    private static bool IsMicronClientSata(DeviceDescriptor drive) =>
        MicronClientSata().Match(drive.Model) is { Success: true } match
        && (!IsM500(match) || IsM500FirmwareCovered(drive.Revision));

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
    private static partial Regex MicronClientSata();

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
