using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real store's handling of what <c>pnputil</c>, the devices and the package folders answer, each
/// stated by the test.
/// </summary>
public sealed class DriverStoreTests : IDisposable
{
    /// <summary>The listing without <c>/devices</c>, holding one extension package.</summary>
    private const string ExtensionListing = """
        <PnpUtil>
            <Driver DriverName="oem100.inf">
                <OriginalName>audioext.inf</OriginalName>
                <ProviderName>Example Audio</ProviderName>
                <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
                <ExtensionId>{c3a63edd-2d27-4b66-b155-5e94b43d926a}</ExtensionId>
                <DriverVersion>10/28/2023 1.0.0.80</DriverVersion>
            </Driver>
        </PnpUtil>
        """;

    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeDriverPackageFolders _folders = new(@"C:\Store");

    public DriverStoreTests() => _system = new FakeSystemDirectories(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary><c>pnputil</c> where the store must run it: the fake answers no other program.</summary>
    private string PnpUtil => DriverStore.PnpUtil(_system);

    private static readonly FakeDeviceDriverReader NoDevices = new();

    [Fact]
    public async Task AsksPnpUtilInTheWindowsDirectoryForTheListingWithoutDevices()
    {
        var runner = new FakeProcessRunner().Responding(PnpUtil, "/enum-drivers", "<PnpUtil />");

        var listing = await new DriverStore(runner, _system, NoDevices, _folders).ListAsync(CancellationToken.None);

        var (fileName, arguments, _) = Assert.Single(runner.Invocations);
        Assert.Equal(Path.Combine(_system.WindowsDirectory, "System32", "pnputil.exe"), fileName);
        Assert.Equal("/enum-drivers /format xml", arguments);
        Assert.Null(listing.Failure);
        Assert.Empty(listing.Packages);
    }

    /// <summary>A device that would not read might use any package, so pnputil is asked to name each one's devices.</summary>
    [Fact]
    public async Task AsksPnpUtilForEachPackagesDevicesWhereTheDevicesDoNotRead()
    {
        var runner = new FakeProcessRunner().Responding(PnpUtil, "/enum-drivers", "<PnpUtil />");

        var listing = await new DriverStore(runner, _system, FakeDeviceDriverReader.Unreadable, _folders).ListAsync(CancellationToken.None);

        Assert.Equal(
            ["/enum-drivers /format xml", "/enum-drivers /devices /format xml"],
            runner.Invocations.Select(i => i.Arguments));
        Assert.Null(listing.Failure);
    }

    /// <summary>
    /// The store holds an extension package and no device reports its extensions, which a Windows too
    /// old to write the property answers too: pnputil names the devices, and its answer is the one used.
    /// </summary>
    [Fact]
    public async Task TakesPnpUtilsDevicesForAnExtensionPackageNoDeviceReadingCovers()
    {
        var withDevices = ExtensionListing.Replace(
            "</DriverVersion>",
            "</DriverVersion><Devices><Device InstanceId=\"USB\\0\" /></Devices>",
            StringComparison.Ordinal);
        var runner = new FakeProcessRunner()
            .Responding(PnpUtil, "/devices", withDevices)
            .Responding(PnpUtil, "/enum-drivers", ExtensionListing);

        var listing = await new DriverStore(runner, _system, new FakeDeviceDriverReader(new DeviceDriverProperties("usbaudio.inf", [])), _folders)
            .ListAsync(CancellationToken.None);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.True(Assert.Single(listing.Packages).InUse);
    }

    [Fact]
    public async Task TakesAnExtensionPackagesUseFromTheDevicesWhereADeviceReportsItsExtensions()
    {
        var runner = new FakeProcessRunner().Responding(PnpUtil, "/enum-drivers", ExtensionListing);
        var devices = new FakeDeviceDriverReader(new DeviceDriverProperties("usbaudio.inf", ["oem100.inf:USB\\0,Install"]));

        var listing = await new DriverStore(runner, _system, devices, _folders).ListAsync(CancellationToken.None);

        Assert.Single(runner.Invocations);
        var package = Assert.Single(listing.Packages);
        Assert.True(package.InUse);
        Assert.Equal(@"C:\Store\oem100.inf", package.Folder);
    }

    [Fact]
    public async Task ReportsAPnpUtilThatFailedAsAFailure()
    {
        var runner = new FakeProcessRunner().Replying(PnpUtil, _ => new CommandOutcome(5, string.Empty, "Access is denied."));

        var listing = await new DriverStore(runner, _system, NoDevices, _folders).ListAsync(CancellationToken.None);

        Assert.Contains("Access is denied.", listing.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAPnpUtilThatFailedToNameTheDevicesAsAFailure()
    {
        var runner = new FakeProcessRunner().Replying(PnpUtil, arguments => arguments.Contains("/devices", StringComparison.Ordinal)
            ? new CommandOutcome(5, string.Empty, "Access is denied.")
            : new CommandOutcome(0, "<PnpUtil />", string.Empty));

        var listing = await new DriverStore(runner, _system, FakeDeviceDriverReader.Unreadable, _folders).ListAsync(CancellationToken.None);

        Assert.Contains("Access is denied.", listing.Failure, StringComparison.Ordinal);
        Assert.Empty(listing.Packages);
    }

    /// <summary>An older pnputil prints its usage for an option it does not know, and exits cleanly.</summary>
    [Fact]
    public async Task ReportsAnAnswerThatIsNotTheListingAsAFailure()
    {
        var runner = new FakeProcessRunner().Responding(PnpUtil, "/enum-drivers", "Microsoft PnP Utility\r\n\r\nPNPUTIL [/add-driver <...>");

        var listing = await new DriverStore(runner, _system, NoDevices, _folders).ListAsync(CancellationToken.None);

        Assert.NotNull(listing.Failure);
        Assert.Empty(listing.Packages);
    }
}
