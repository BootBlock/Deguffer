using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health;

/// <summary>What one protocol's reading of a drive gave.</summary>
/// <param name="Attributes">Its readings, the primary signal first where there is one.</param>
/// <param name="HasPrimarySignal">Whether the drive gave its own overall judgement.</param>
/// <param name="Facts">Details only this protocol can give.</param>
/// <param name="TemperatureCelsius">The drive's temperature, or null where it did not say.</param>
/// <param name="MoreWhenElevated">Whether a question was refused for want of administrator rights.</param>
internal sealed record DriveReadout(
    IReadOnlyList<HealthAttribute> Attributes,
    bool HasPrimarySignal,
    IReadOnlyList<DriveFact> Facts,
    int? TemperatureCelsius,
    bool MoreWhenElevated);

/// <summary>Reads a drive's health in the way one storage protocol allows.</summary>
internal interface IDriveHealthSource
{
    /// <summary>Whether this source can read a drive on <paramref name="bus"/>.</summary>
    bool Reads(StorageBusType bus);

    /// <summary>
    /// Reads <paramref name="drive"/>, whose model and firmware decide what some readings mean. A
    /// reading counted in logical sectors is stated in bytes only where <paramref name="sectors"/> is known.
    /// </summary>
    DriveReadout Read(IDriveDevice device, DeviceDescriptor drive, SectorSizes? sectors);
}
