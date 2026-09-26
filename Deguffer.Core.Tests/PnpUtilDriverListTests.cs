using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading <c>pnputil /enum-drivers /devices /format xml</c>. The fixture keeps the shape Windows 11
/// 24H2 writes, with invented names.
/// </summary>
public sealed class PnpUtilDriverListTests
{
    private const string Listing = """
        <?xml version="1.0" encoding="utf-8"?>
        <PnpUtil Version="10.0.26100" Command="/enum-drivers /devices /format xml">
            <Driver DriverName="oem12.inf">
                <OriginalName>wifi.inf</OriginalName>
                <ProviderName>Example Networks</ProviderName>
                <ClassName>Net</ClassName>
                <ClassGuid>{4d36e972-e325-11ce-bfc1-08002be10318}</ClassGuid>
                <DriverVersion>07/22/2022 23.40.0.4</DriverVersion>
                <SignerName>Microsoft Windows Hardware Compatibility Publisher</SignerName>
                <CatalogFile>wifi.cat</CatalogFile>
            </Driver>
            <Driver DriverName="oem100.inf">
                <OriginalName>audioext.inf</OriginalName>
                <ProviderName>Example Audio</ProviderName>
                <ClassName>Extension</ClassName>
                <ClassGuid>{e2f84ce7-8efa-411c-aa69-97454ca4cb57}</ClassGuid>
                <ExtensionId>{c3a63edd-2d27-4b66-b155-5e94b43d926a}</ExtensionId>
                <DriverVersion>10/28/2024 1.0.0.85</DriverVersion>
                <Devices>
                    <Device InstanceId="USB\VID_0000&amp;PID_0000\0">
                        <DeviceDescription>USB Audio</DeviceDescription>
                        <Status>Started</Status>
                    </Device>
                </Devices>
            </Driver>
        </PnpUtil>
        """;

    private static string? Nowhere(string _) => null;

    [Fact]
    public void ReadsEachPackageFromTheListing()
    {
        var (packages, unread) = PnpUtilDriverList.Parse(Listing, name => $@"C:\Store\{name}")!.Value;

        Assert.Equal(0, unread);
        Assert.Equal(2, packages.Count);

        var wifi = packages[0];
        Assert.Equal("oem12.inf", wifi.PublishedName);
        Assert.Equal("wifi.inf", wifi.OriginalName);
        Assert.Equal("Example Networks", wifi.Provider);
        Assert.Equal("{4d36e972-e325-11ce-bfc1-08002be10318}", wifi.ClassGuid);
        Assert.Null(wifi.ExtensionId);
        Assert.False(wifi.InUse);
        Assert.Equal(@"C:\Store\oem12.inf", wifi.Folder);

        var extension = packages[1];
        Assert.Equal("{c3a63edd-2d27-4b66-b155-5e94b43d926a}", extension.ExtensionId);
        Assert.True(extension.InUse);
    }

    /// <summary>
    /// Month first, whatever this machine's date format: 07/22/2022 is the 22nd of July, and a
    /// day-first reading would not parse it at all.
    /// </summary>
    [Fact]
    public void ReadsTheDateMonthFirst()
    {
        var (packages, _) = PnpUtilDriverList.Parse(Listing, Nowhere)!.Value;

        Assert.Equal(new DateOnly(2022, 7, 22), packages[0].Date);
        Assert.Equal(new Version(23, 40, 0, 4), packages[0].Version);
    }

    /// <summary>An entry whose version does not read is counted and left out, never given a guessed age.</summary>
    [Fact]
    public void LeavesOutAndCountsAnEntryThatDoesNotRead()
    {
        var damaged = Listing.Replace("07/22/2022 23.40.0.4", "22/07/2022 23.40.0.4", StringComparison.Ordinal);

        var (packages, unread) = PnpUtilDriverList.Parse(damaged, Nowhere)!.Value;

        Assert.Equal(1, unread);
        Assert.Equal("oem100.inf", Assert.Single(packages).PublishedName);
    }

    /// <summary>An older pnputil that does not know <c>/format</c> prints its usage, which is not the listing.</summary>
    [Theory]
    [InlineData("Microsoft PnP Utility\r\n\r\nPNPUTIL [/add-driver <...> | /delete-driver <...> |")]
    [InlineData("<?xml version=\"1.0\"?><Other />")]
    public void RefusesTextThatIsNotTheListing(string text)
    {
        Assert.Null(PnpUtilDriverList.Parse(text, Nowhere));
    }
}
