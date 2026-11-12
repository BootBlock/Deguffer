namespace Deguffer.Core.Safety;

/// <summary>What the configuration manager says one device is installed with.</summary>
/// <param name="InfPath">
/// <c>DEVPKEY_Device_DriverInfPath</c>: the INF of the device's own driver, <c>oem12.inf</c>, or null
/// where it has none.
/// </param>
/// <param name="ExtendedConfigurationIds">
/// <c>DEVPKEY_Device_ExtendedConfigurationIds</c>: one entry for each extension INF applied to the
/// device, <c>oem141.inf:PCI\VEN_8086&amp;DEV_7A50,Install,09/03/2026,32.0.101.7092</c>, empty where
/// none is.
/// </param>
public sealed record DeviceDriverProperties(string? InfPath, IReadOnlyList<string> ExtendedConfigurationIds);

/// <summary>
/// The driver packages some device is installed with, its own driver and its extensions both, read
/// from every device's properties. It answers what <c>pnputil /enum-drivers /devices</c> answers for
/// about a seventh of pnputil's cost, which is almost all spent enumerating each package's devices.
///
/// <para><b>An extension package it cannot vouch for is not classified.</b> Nothing but
/// <c>DEVPKEY_Device_ExtendedConfigurationIds</c> names the extension INFs applied to a device, and a
/// Windows too old to write it answers exactly as one with no extensions applied. So where no device
/// reports the property, an extension package's use is unknown, and the listing that names each
/// package's devices is asked instead. Reporting a package unused that a device uses would offer it
/// for removal.</para>
/// </summary>
public sealed class DeviceDrivers
{
    private const string InfEnd = ".inf:";

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private readonly HashSet<string> _packages;
    private readonly bool _reportsExtensions;

    private DeviceDrivers(HashSet<string> packages, bool reportsExtensions)
    {
        _packages = packages;
        _reportsExtensions = reportsExtensions;
    }

    /// <param name="devices">Every device Windows knows, present or not.</param>
    public static DeviceDrivers Of(IEnumerable<DeviceDriverProperties> devices)
    {
        var packages = new HashSet<string>(Names);
        var reportsExtensions = false;

        foreach (var device in devices)
        {
            if (Inf(device.InfPath) is { } inf)
            {
                packages.Add(inf);
            }

            foreach (var configuration in device.ExtendedConfigurationIds)
            {
                reportsExtensions = true;

                // The INF ends where its name meets the colon before the hardware ID, which a drive's
                // colon in a path does not. An entry without one is taken whole, which can only add a
                // package that is in use.
                var end = configuration.IndexOf(InfEnd, StringComparison.OrdinalIgnoreCase);

                if (Inf(end < 0 ? configuration : configuration[..(end + InfEnd.Length - 1)]) is { } extension)
                {
                    packages.Add(extension);
                }
            }
        }

        return new DeviceDrivers(packages, reportsExtensions);
    }

    /// <summary>Whether some device is installed with the package Windows calls <paramref name="publishedName"/>.</summary>
    public bool Uses(string publishedName) => _packages.Contains(publishedName);

    /// <summary>Whether <see cref="Uses"/> answers for <paramref name="package"/>. See the type's remarks.</summary>
    public bool Classifies(DriverPackage package) => package.ExtensionId is null || _reportsExtensions;

    /// <summary>The INF's file name. Windows writes the published name alone, and a path is reduced to it.</summary>
    private static string? Inf(string? text) =>
        Path.GetFileName(text?.Trim()) is { Length: > 0 } name ? name : null;
}
