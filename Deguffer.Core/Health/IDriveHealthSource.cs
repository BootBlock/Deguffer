using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Health.Smartmontools;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>What one protocol's reading of a drive gave.</summary>
/// <param name="Attributes">Its readings, the primary signal first where there is one.</param>
/// <param name="HasPrimarySignal">Whether the drive gave its own overall judgement.</param>
/// <param name="Facts">Details only this protocol can give.</param>
/// <param name="DatabaseWarning">The installed drive database's warning about the drive's family, where it gives one.</param>
/// <param name="TemperatureCelsius">The drive's temperature, or null where it did not say.</param>
/// <param name="MoreWhenElevated">Whether a question was refused for want of administrator rights.</param>
internal sealed record DriveReadout(
    IReadOnlyList<HealthAttribute> Attributes,
    bool HasPrimarySignal,
    IReadOnlyList<DriveFact> Facts,
    DatabaseWarning? DatabaseWarning,
    int? TemperatureCelsius,
    bool MoreWhenElevated);

/// <summary>What the reader knows of a drive before its health is read, each asked of the drive once.</summary>
/// <param name="Sectors">Its sector sizes, by which a reading counted in logical sectors is stated in bytes, where known.</param>
/// <param name="IncursSeekPenalty">Whether Windows reports it as a spinning disk, or null where it did not say.</param>
/// <param name="Database">The installed drive database, read at most once for a whole read of the drives, and only where a drive needs it.</param>
internal sealed record DriveTraits(SectorSizes? Sectors, bool? IncursSeekPenalty, Lazy<DatabaseReading> Database);

/// <summary>Reads a drive's health in the way one storage protocol allows.</summary>
internal interface IDriveHealthSource
{
    /// <summary>Whether this source can read a drive on <paramref name="bus"/>.</summary>
    bool Reads(StorageBusType bus);

    /// <summary>Reads <paramref name="drive"/>, whose model and firmware decide what some readings mean.</summary>
    DriveReadout Read(IDriveDevice device, DeviceDescriptor drive, DriveTraits traits);
}
