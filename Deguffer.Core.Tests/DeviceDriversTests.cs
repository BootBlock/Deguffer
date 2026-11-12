using System.Xml.Linq;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which packages are in use, read from the devices' own properties, held to what pnputil says when it
/// is asked to name each package's devices. The listing keeps the shape Windows 11 24H2 writes, with
/// invented names, and the devices are the properties the same machine reports for it.
/// </summary>
public sealed class DeviceDriversTests
{
    private const string Repository = @"C:\Windows\System32\DriverStore\FileRepository";

    private const string AudioExtension = "{c3a63edd-2d27-4b66-b155-5e94b43d926a}";

    /// <summary>
    /// <c>pnputil /enum-drivers /devices /format xml</c>: an older wireless driver a device uses and a
    /// newer one, an older wireless driver nothing uses, an older audio extension a device has applied
    /// and a newer one, and a package used only by a device that is not connected.
    /// </summary>
    private const string ListingWithDevices = """
        <?xml version="1.0" encoding="utf-8"?>
        <PnpUtil Version="10.0.26100" Command="/enum-drivers /devices /format xml">
            <Driver DriverName="oem7.inf">
                <OriginalName>wifi.inf</OriginalName>
                <ProviderName>Example Networks</ProviderName>
                <ClassName>Net</ClassName>
                <ClassGuid>{4d36e972-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>01/10/2021 22.10.0.1</DriverVersion>
            </Driver>
            <Driver DriverName="oem12.inf">
                <OriginalName>wifi.inf</OriginalName>
                <ProviderName>Example Networks</ProviderName>
                <ClassName>Net</ClassName>
                <ClassGuid>{4d36e972-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>07/22/2022 23.40.0.4</DriverVersion>
                <Devices>
                    <Device InstanceId="PCI\VEN_0000&amp;DEV_0001\0">
                        <DeviceDescription>Wireless adapter</DeviceDescription>
                        <Status>Started</Status>
                    </Device>
                </Devices>
            </Driver>
            <Driver DriverName="oem13.inf">
                <OriginalName>wifi.inf</OriginalName>
                <ProviderName>Example Networks</ProviderName>
                <ClassName>Net</ClassName>
                <ClassGuid>{4d36e972-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>03/02/2024 23.60.0.2</DriverVersion>
            </Driver>
            <Driver DriverName="oem100.inf">
                <OriginalName>audioext.inf</OriginalName>
                <ProviderName>Example Audio</ProviderName>
                <ClassName>Extension</ClassName>
                <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
                <ExtensionId>{c3a63edd-2d27-4b66-b155-5e94b43d926a}</ExtensionId>
                <DriverVersion>10/28/2023 1.0.0.80</DriverVersion>
                <Devices>
                    <Device InstanceId="USB\VID_0000&amp;PID_0000\0">
                        <DeviceDescription>USB Audio</DeviceDescription>
                        <Status>Started</Status>
                    </Device>
                </Devices>
            </Driver>
            <Driver DriverName="oem101.inf">
                <OriginalName>audioext.inf</OriginalName>
                <ProviderName>Example Audio</ProviderName>
                <ClassName>Extension</ClassName>
                <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
                <ExtensionId>{c3a63edd-2d27-4b66-b155-5e94b43d926a}</ExtensionId>
                <DriverVersion>10/28/2024 1.0.0.85</DriverVersion>
            </Driver>
            <Driver DriverName="oem30.inf">
                <OriginalName>monitor.inf</OriginalName>
                <ProviderName>Example Displays</ProviderName>
                <ClassName>Monitor</ClassName>
                <ClassGuid>{4d36e96e-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>05/05/2020 1.0.0.0</DriverVersion>
                <Devices>
                    <Device InstanceId="DISPLAY\EXA0000\1&amp;0&amp;0&amp;UID0">
                        <DeviceDescription>Example monitor</DeviceDescription>
                        <Status>Disconnected</Status>
                    </Device>
                </Devices>
            </Driver>
        </PnpUtil>
        """;

    /// <summary>The same machine's devices: the three that name a package, and two on drivers that are part of Windows.</summary>
    private static readonly DeviceDriverProperties[] Devices =
    [
        new("oem12.inf", []),
        new("usbaudio.inf", [@"oem100.inf:USB\VID_0000&PID_0000,Extension_Install,10/28/2023,1.0.0.80"]),
        new("OEM30.INF", []),
        new("input.inf", []),
        new(null, []),
    ];

    /// <summary>The listing pnputil writes without <c>/devices</c>: the same, with no package's devices named.</summary>
    private static string ListingWithoutDevices()
    {
        var document = XDocument.Parse(ListingWithDevices);
        document.Descendants("Devices").Remove();
        document.Root!.SetAttributeValue("Command", PnpUtilDriverList.Arguments);
        return document.ToString();
    }

    private static string InRepository(string name) => Path.Combine(Repository, name);

    private static IReadOnlyList<DriverPackage> FromDevices(DeviceDrivers devices) =>
        PnpUtilDriverList.Parse(ListingWithoutDevices(), InRepository, devices.Uses)!.Value.Packages;

    private static string[] InUse(IEnumerable<DriverPackage> packages) =>
        [.. packages.Where(p => p.InUse).Select(p => p.PublishedName).Order(StringComparer.OrdinalIgnoreCase)];

    [Fact]
    public void FindsEveryPackagePnpUtilFindsInUseAndNoOther()
    {
        var named = PnpUtilDriverList.ParseWithDevices(ListingWithDevices, InRepository)!.Value.Packages;

        var read = FromDevices(DeviceDrivers.Of(Devices));

        Assert.Equal(["oem100.inf", "oem12.inf", "oem30.inf"], InUse(named));
        Assert.Equal(InUse(named), InUse(read));
    }

    /// <summary>§5.6: an older package a device uses, as its driver or as an extension, is never offered.</summary>
    [Fact]
    public void NeverOffersAnOlderPackageADeviceUses()
    {
        var drivers = SupersededDrivers.Of(FromDevices(DeviceDrivers.Of(Devices)), Repository);

        Assert.Equal(["oem7.inf"], drivers.Superseded.Select(s => s.Package.PublishedName));
        Assert.Equal(["oem100.inf", "oem12.inf"], drivers.InUse.Select(p => p.PublishedName));
    }

    /// <summary>The extension is named only by its configuration: the device's own driver is another package.</summary>
    [Fact]
    public void TakesAnExtensionFromItsConfigurationNotTheDevicesOwnDriver()
    {
        var devices = DeviceDrivers.Of([new("usbaudio.inf", ["oem100.inf:USB\\VID_0000&PID_0000,Extension_Install"])]);

        Assert.True(devices.Uses("oem100.inf"));
        Assert.True(devices.Uses("usbaudio.inf"));
        Assert.False(devices.Uses("oem101.inf"));
    }

    /// <summary>A path is reduced to its name, and an entry with no hardware ID is taken whole, never dropped.</summary>
    [Theory]
    [InlineData(@"C:\Windows\INF\oem12.inf", "oem12.inf")]
    [InlineData(" oem12.inf ", "oem12.inf")]
    public void ReadsTheInfsNameFromWhatWindowsWrites(string written, string name)
    {
        Assert.True(DeviceDrivers.Of([new(written, [])]).Uses(name));
        Assert.True(DeviceDrivers.Of([new(null, [written])]).Uses(name));
        Assert.True(DeviceDrivers.Of([new(null, [written.Trim() + @":USB\VID_0000&PID_0000,Install"])]).Uses(name));
    }

    /// <summary>
    /// No device reports its extensions, which is also what a Windows too old to write the property
    /// answers: an extension package's use is unknown, and only a package that is not an extension is
    /// classified.
    /// </summary>
    [Fact]
    public void DoesNotClassifyAnExtensionPackageWhereNoDeviceReportsItsExtensions()
    {
        var devices = DeviceDrivers.Of([new("oem12.inf", [])]);
        var packages = FromDevices(devices);

        Assert.False(devices.Classifies(packages.Single(p => p.PublishedName == "oem100.inf")));
        Assert.True(devices.Classifies(packages.Single(p => p.PublishedName == "oem12.inf")));
    }

    [Fact]
    public void ClassifiesEveryPackageWhereSomeDeviceReportsItsExtensions()
    {
        var devices = DeviceDrivers.Of(Devices);

        Assert.All(FromDevices(devices), package => Assert.True(devices.Classifies(package)));
        Assert.Contains(FromDevices(devices), p => p.ExtensionId == AudioExtension);
    }
}
