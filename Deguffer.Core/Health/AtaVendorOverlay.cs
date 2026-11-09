namespace Deguffer.Core.Health;

/// <summary>
/// The attributes a named manufacturer documents more exactly than the catalogue can state for every
/// drive. Only these are read as more than opaque numbers, because the same ID means something else
/// on another maker's drive.
/// </summary>
internal static class AtaVendorOverlay
{
    private static readonly AtaAttributeDefinition SamsungWear = new(
        "Remaining life", "How much of the drive's rated write life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition SamsungWritten = new(
        "Total data written", "How much the computer has written to the drive over its life.", SmartRawFormat.Bytes512, AttributeRole.Usage);

    private static readonly AtaAttributeDefinition SamsungRead = new(
        "Total data read", "How much the computer has read from the drive over its life.", SmartRawFormat.Bytes512, AttributeRole.Usage);

    private static readonly AtaAttributeDefinition RemainingPercent = new(
        "Remaining life", "How much of the drive's rated write life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    private static readonly AtaAttributeDefinition MediaWearout = new(
        "Media wear indicator", "How much of the flash memory's rated life is left.", SmartRawFormat.Percent, AttributeRole.RemainingLife);

    /// <summary>The overlay's definition for <paramref name="id"/> on the drive named <paramref name="model"/>, if any.</summary>
    internal static AtaAttributeDefinition? Of(byte id, string model) => id switch
    {
        177 when Is(model, "SAMSUNG") => SamsungWear,
        241 when Is(model, "SAMSUNG") => SamsungWritten,
        242 when Is(model, "SAMSUNG") => SamsungRead,
        202 when Is(model, "CT") || Is(model, "Crucial") || Is(model, "MTFD") || Is(model, "Micron") => RemainingPercent,
        233 when Is(model, "INTEL") => MediaWearout,
        _ => null,
    };

    private static bool Is(string model, string prefix) => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
