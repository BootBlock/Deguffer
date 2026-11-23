using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health.Smartmontools;

/// <summary>
/// Looks one ATA drive up in the installed drive database, and says what was found as a detail of the
/// drive, so the reader can see which family's entry named the readings marked as the database's, or
/// why none did, and gives the entry's warning where it has one. A machine without smartmontools gets
/// none of these.
/// </summary>
internal static class DriveDatabaseLookup
{
    internal const string FactLabel = "smartmontools drive database";

    /// <summary>
    /// The strings smartctl would search by: IDENTIFY's model and firmware where an administrator could
    /// read them, else Windows' description of the drive.
    /// </summary>
    internal static DriveIdentity Identity(DeviceDescriptor drive, AtaIdentify? identify) =>
        identify is { Model.Length: > 0 } read
            ? new DriveIdentity(read.Model, null, read.Firmware)
            : new DriveIdentity(drive.Model, drive.Vendor.Length > 0 ? drive.Product : null, drive.Revision);

    internal static (DatabaseFamily? Family, DriveFact? Fact, DatabaseWarning? Warning) For(DriveIdentity drive, DriveTraits traits) =>
        traits.Database.Value.Of(drive) is { } answer
            ? (answer.Family(Medium(traits.IncursSeekPenalty)), new DriveFact(FactLabel, answer.Detail), answer.Warning)
            : (null, null, null);

    private static DriveMedium Medium(bool? incursSeekPenalty) => incursSeekPenalty switch
    {
        true => DriveMedium.HardDisk,
        false => DriveMedium.SolidState,
        null => DriveMedium.Any,
    };
}
