using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Devices whose driver properties a test states. The real reader reads the devices of whoever runs
/// the suite.
/// </summary>
/// <param name="devices">Every device, or null for a reading that failed.</param>
public sealed class FakeDeviceDriverReader(IReadOnlyList<DeviceDriverProperties>? devices) : IDeviceDriverReader
{
    public FakeDeviceDriverReader(params DeviceDriverProperties[] devices)
        : this((IReadOnlyList<DeviceDriverProperties>)devices)
    {
    }

    /// <summary>A reading in which some device would not read.</summary>
    public static FakeDeviceDriverReader Unreadable { get; } = new((IReadOnlyList<DeviceDriverProperties>?)null);

    public IReadOnlyList<DeviceDriverProperties>? Read(CancellationToken ct) => devices;
}
