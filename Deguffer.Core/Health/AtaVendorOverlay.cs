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

    /// <summary>The overlay's definition for <paramref name="id"/> on <paramref name="drive"/>, if a document covers it.</summary>
    internal static AtaAttributeDefinition? Of(byte id, DeviceDescriptor drive) =>
        MicronClientSata.Covers(drive) ? MicronClientSata.Of(id) : Other(id, drive);

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

    private static AtaAttributeDefinition? Other(byte id, DeviceDescriptor drive) => id switch
    {
        // Samsung, "Using SMART Attributes to Estimate Drive Lifetime" (WHP-SSD-SSDSMARTATTRIBUTES-APR16J):
        // the normalised value is the percentage of remaining P/E cycles, on the PM863 and SM863.
        177 when SamsungPm863OrSm863().IsMatch(drive.Model) => RemainingLife,

        // The same white paper, and Samsung's S.M.A.R.T. application note for the 845 DC PRO and EVO:
        // the raw value counts 512-byte sectors. Neither states a unit for 242, so 242 stays as reported.
        241 when SamsungPm863OrSm863().IsMatch(drive.Model) || Samsung845Dc().IsMatch(drive.Model) => TotalWritten,

        // Intel DC S3500 product specification 328860-008US, table 18: the normalised value declines
        // from 100 to 1 with wear. Solidigm KA-00033 names 233 for later drives but states no scale.
        233 when IntelDcS3500().IsMatch(drive.Model) => MediaWearout,

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
