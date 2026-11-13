using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// Kingston's SMART attribute documents for its SATA drives. They differ from each other in where a
/// figure sits, which way it counts and what unit it is in, so each is keyed on the models it names
/// and read by its own table. A model no document names keeps the catalogue's reading.
/// </summary>
internal static partial class KingstonSata
{
    private const string TemperatureExplanation = "The temperature the drive measures inside itself, with the lowest and highest it has recorded.";

    private static readonly AtaAttributeDefinition TemperatureLowestFirst = new(
        "Temperature", TemperatureExplanation, SmartRawFormat.CelsiusRange, AttributeRole.Temperature);

    /// <summary>
    /// MKP-306.2 (2015), for SandForce SF-2000 based drives. Several of its rows print the raw-value text
    /// of the attribute before or after them, so each row read here is matched by what its text describes:
    /// the raw usage for 177 is printed under 181. 177 counts the spread of wear, not wear levelling, and
    /// 194 puts the highest temperature before the lowest. 241 and 242 fill the low four bytes, and 242
    /// counts in steps of 64 GB. The document gives 177 as a percentage in bytes 0-3. It is read from byte 0,
    /// as the shared wear range is, which holds it whole up to 255%.
    /// </summary>
    private static readonly Document SandForce = new(SandForceModels(), new Dictionary<byte, AtaAttributeDefinition>
    {
        [177] = AtaVendorOverlay.WearRange,
        [194] = new("Temperature", TemperatureExplanation, SmartRawFormat.CelsiusRangeHighestFirst, AttributeRole.Temperature),
        [231] = AtaVendorOverlay.RemainingLife,
        [241] = AtaVendorOverlay.GigabytesWritten,
        [242] = AtaVendorOverlay.GigabytesRead with { Explanation = $"{AtaVendorOverlay.GigabytesRead.Explanation} The drive counts in steps of 64 GB." },
    }.ToFrozenDictionary());

    /// <summary>The layout MKP-521.5 and MKP-521.8US share for the attributes Deguffer reads.</summary>
    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> PhisonAttributes = new Dictionary<byte, AtaAttributeDefinition>
    {
        [194] = TemperatureLowestFirst,
        [231] = AtaVendorOverlay.RemainingLifeInLowByte,
        [241] = AtaVendorOverlay.GigabytesWritten with { Width = RawWidth.AllSixBytes },
        [242] = AtaVendorOverlay.GigabytesRead with { Width = RawWidth.AllSixBytes },
    }.ToFrozenDictionary();

    /// <summary>
    /// MKP-521.5 (2016), for the Phison based KC310, UV300, KC400, HyperX Savage, V310, SM2280S3G2 and
    /// DC400. 231 is the percentage left in raw byte 0, and 241 and 242 fill all six bytes.
    /// </summary>
    private static readonly Document Phison2016 = new(Phison2016Models(), PhisonAttributes);

    /// <summary>
    /// MKP-521.8US (2023), for the DC450R, DC500R, DC500M and DC600M. It lays out 194, 231, 241 and 242
    /// as MKP-521.5 does.
    /// </summary>
    private static readonly Document Phison2023 = new(Phison2023Models(), PhisonAttributes);

    /// <summary>
    /// The UV500 SMART attribute sheet (2018). 231 is read by its normalised value. The sheet does not
    /// say how wide 241 and 242 are, so they are read from the low four bytes, as the catalogue reads
    /// any count.
    /// </summary>
    private static readonly Document Uv500 = new(Uv500Models(), new Dictionary<byte, AtaAttributeDefinition>
    {
        [194] = TemperatureLowestFirst,
        [231] = AtaVendorOverlay.RemainingLife,
        [241] = AtaVendorOverlay.GigabytesWritten,
        [242] = AtaVendorOverlay.GigabytesRead,
    }.ToFrozenDictionary());

    /// <summary>
    /// MKF-895 (2019), for the KC600, which counts 241 and 242 in logical sectors. The sheet describes
    /// 231 both as the life used and as the life left, so 231 keeps the catalogue's uninterpreted
    /// reading. It gives no byte layout for 194, so 194 keeps the catalogue's reading too.
    /// </summary>
    private static readonly Document Kc600 = new(Kc600Models(), new Dictionary<byte, AtaAttributeDefinition>
    {
        [241] = new("Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.LogicalSectors, AttributeRole.Usage),
        [242] = new("Total data read", "How much the computer has read from the drive over its life.", SmartRawFormat.LogicalSectors, AttributeRole.Usage),
    }.ToFrozenDictionary());

    private static readonly Document[] Documents = [SandForce, Phison2016, Phison2023, Uv500, Kc600];

    /// <summary>
    /// The meaning that the document naming <paramref name="drive"/> gives <paramref name="id"/>, or null
    /// where no document names the drive or its document leaves the attribute out.
    /// </summary>
    internal static AtaAttributeDefinition? Of(byte id, DeviceDescriptor drive)
    {
        foreach (var document in Documents)
        {
            if (document.Models.IsMatch(drive.Model))
            {
                return document.Attributes.GetValueOrDefault(id);
            }
        }

        return null;
    }

    /// <summary>The models one document names, and what it says each attribute means on them.</summary>
    private sealed record Document(Regex Models, FrozenDictionary<byte, AtaAttributeDefinition> Attributes);

    /// <summary>
    /// The KC100 (SKC100S3) and KC300 (SKC300S37A), the only SF-2000 models MKP-306.2 names. Kingston's
    /// other SF-2000 drives are not named, so they are not covered.
    /// </summary>
    [GeneratedRegex(@"\bSKC(?:100S3|300S37A)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SandForceModels();

    /// <summary>The part number prefixes MKP-521.5 lists, each followed in a model name by the capacity.</summary>
    [GeneratedRegex(@"\b(?:SKC310S37A|SUV300S37A|SKC400S37|SHSS37A|SV310S37A|SM2280S3G2|SEDC400S37)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Phison2016Models();

    /// <summary>The DC450R, DC500R, DC500M and DC600M by part number (SEDC450R and so on), then the capacity.</summary>
    [GeneratedRegex(@"\bSEDC(?:450R|500R|500M|600M)\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Phison2023Models();

    /// <summary>
    /// The SUV500, SUV500M8 and SUV500MS the sheet names, then the capacity, so that a later model whose
    /// part number starts SUV500 is not taken for one of them.
    /// </summary>
    [GeneratedRegex(@"\bSUV500(?:M8|MS)?\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uv500Models();

    /// <summary>The KC600 by part number: SKC600 (2.5-inch) or SKC600MS (mSATA), then the capacity.</summary>
    [GeneratedRegex(@"\bSKC600(?:MS)?\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Kc600Models();
}
