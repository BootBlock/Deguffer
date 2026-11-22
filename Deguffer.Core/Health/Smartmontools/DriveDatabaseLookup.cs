using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// Looks one ATA drive up in the installed drive database, and says what was found as a detail of the
/// drive, so the reader can see which family's entry named the readings marked as the database's, or
/// why none did. A machine without smartmontools gets neither.
/// </summary>
internal static class DriveDatabaseLookup
{
    internal const string FactLabel = "smartmontools drive database";

    internal static (DatabaseFamily? Family, DriveFact? Fact) For(DeviceDescriptor drive, DriveTraits traits)
    {
        var reading = traits.Database.Value;

        if (reading is not { State: DatabaseState.Loaded, Database: { } database })
        {
            return reading.State is DatabaseState.Unreadable
                ? (null, Fact(reading.FailedLine > 0 ? $"{reading.FailedFile} could not be read at line {reading.FailedLine}, so none of it is used" : $"{reading.FailedFile} could not be read, so none of it is used"))
                : (null, null);
        }

        var answer = Find(database, drive);

        return answer switch
        {
            { Verdict: DatabaseVerdict.Found, Family: { } family, Presets: { } presets } =>
                (new DatabaseFamily(family, presets, answer.Defaults, Medium(traits.IncursSeekPenalty)), Fact(family)),
            { Verdict: DatabaseVerdict.EntryUnreadable, Family: { } family } => (null, Fact($"The entry for {family} could not be read, so it is not used")),
            { Verdict: DatabaseVerdict.CannotTell } => (null, Fact("An entry searched before this drive's could not be read, so no entry is used")),
            _ => (null, Fact("No entry for this drive")),
        };
    }

    /// <summary>
    /// smartctl matches the model string from IDENTIFY. Windows gives a SATA drive's model as its product,
    /// and where it also gives a vendor, the product alone may be what IDENTIFY holds, so it is tried next.
    /// </summary>
    private static DatabaseAnswer Find(DriveDatabase database, DeviceDescriptor drive)
    {
        var answer = database.Find(drive.Model, drive.Revision);

        return answer.Verdict is DatabaseVerdict.NotListed && drive.Vendor.Length > 0
            ? database.Find(drive.Product, drive.Revision)
            : answer;
    }

    private static DriveMedium Medium(bool? incursSeekPenalty) => incursSeekPenalty switch
    {
        true => DriveMedium.HardDisk,
        false => DriveMedium.SolidState,
        null => DriveMedium.Any,
    };

    private static DriveFact Fact(string value) => new(FactLabel, value);
}
