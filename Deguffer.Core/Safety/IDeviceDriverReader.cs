namespace Deguffer.Core.Safety;

/// <summary>
/// Every device's driver properties. Behind an interface because the real one reads the devices of
/// whoever runs the suite, and which packages are in use decides which ones a plan can offer.
/// </summary>
public interface IDeviceDriverReader
{
    /// <summary>
    /// The properties of every device Windows knows, present or not, or null where any device could not
    /// be read: a device whose driver is unknown might be using any package.
    /// </summary>
    IReadOnlyList<DeviceDriverProperties>? Read(CancellationToken ct);
}
