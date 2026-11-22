namespace Deguffer.Core.Health.Smartmontools;

/// <summary>An attribute the drive database lays out and names, which the page shows and does not judge.</summary>
internal sealed record DatabaseAttributeDefinition(string Name, string Explanation, AttributePreset Preset)
    : AtaAttributeDefinition(Name, Explanation, SmartRawFormat.DriveDatabase, AttributeRole.Information);

/// <summary>
/// What the database's entry for one drive's family says about each attribute. Only the entry's own
/// options are used: the <c>DEFAULT</c> entry is smartctl's general naming, which is the guess the
/// catalogue declines for an attribute whose meaning differs by maker, so it only names an attribute an
/// entry's option lays out and leaves unnamed.
/// </summary>
internal sealed class DatabaseFamily(string family, EntryPresets presets, EntryPresets? defaults, DriveMedium medium)
{
    private const string UnnamedInDatabase = "Unknown_Attribute";

    internal string Family => family;

    /// <summary>The database's reading of <paramref name="id"/>, where the entry has an option for it.</summary>
    internal DatabaseAttributeDefinition? Of(byte id)
    {
        if (presets.Of(id) is not { } preset)
        {
            return null;
        }

        var name = preset.Name ?? (defaults?.Of(id) is { } general && AppliesTo(general) ? general.Name : null);

        return new DatabaseAttributeDefinition(
            name is null or UnnamedInDatabase ? $"Vendor-specific attribute {id}" : name.Replace('_', ' '),
            $"The smartmontools drive database on this computer reads this attribute for {family}. No manufacturer's document Deguffer cites covers it, so Deguffer shows the figure as read and does not judge it.",
            preset);
    }

    /// <summary>
    /// Whether a general name marked for hard disks or solid-state drives fits this drive. A drive whose
    /// kind Windows did not report takes either, as smartctl does for a drive whose kind it cannot tell.
    /// </summary>
    private bool AppliesTo(AttributePreset general) =>
        general.Only is DriveMedium.Any || medium is DriveMedium.Any || general.Only == medium;
}
