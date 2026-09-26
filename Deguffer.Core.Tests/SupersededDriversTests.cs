using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which packages in the driver store have a newer version of themselves beside it. Every doubt
/// keeps a package, so most of these are cases that must <em>not</em> be offered.
/// </summary>
public sealed class SupersededDriversTests
{
    private const string Store = @"C:\Windows\System32\DriverStore\FileRepository";
    private const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static DriverPackage Package(
        string published,
        string date,
        string version = "1.0.0.0",
        string original = "wifi.inf",
        string provider = "Example Networks",
        string classGuid = NetClass,
        string? extension = null,
        bool inUse = false,
        string? folder = "") =>
        new(
            published,
            original,
            provider,
            classGuid,
            extension,
            DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
            Version.Parse(version),
            inUse,
            folder == "" ? Path.Combine(Store, $"{original}_amd64_{published}") : folder);

    private static IReadOnlyList<string> Offered(params DriverPackage[] packages) =>
        [.. SupersededDrivers.Of(packages, Store).Superseded.Select(s => s.Package.PublishedName)];

    [Fact]
    public void OffersEveryOlderVersionAndKeepsTheNewest()
    {
        var drivers = SupersededDrivers.Of(
            [Package("oem1.inf", "2022-01-01"), Package("oem2.inf", "2024-01-01"), Package("oem3.inf", "2023-01-01")],
            Store);

        Assert.Equal(["oem1.inf", "oem3.inf"], drivers.Superseded.Select(s => s.Package.PublishedName).Order());
        Assert.All(drivers.Superseded, s => Assert.Equal("oem2.inf", s.Newest.PublishedName));
        Assert.Equal("oem2.inf", Assert.Single(drivers.Newest).PublishedName);
    }

    /// <summary>Windows ranks a driver by its date first, so a later date wins over a higher version.</summary>
    [Fact]
    public void ComparesTheDateBeforeTheVersion()
    {
        Assert.Equal(
            ["oem1.inf"],
            Offered(Package("oem1.inf", "2022-01-01", "9.0.0.0"), Package("oem2.inf", "2023-01-01", "1.0.0.0")));

        Assert.Equal(
            ["oem1.inf"],
            Offered(Package("oem1.inf", "2023-01-01", "1.0.0.0"), Package("oem2.inf", "2023-01-01", "1.0.0.1")));
    }

    /// <summary>Two packages sharing the newest stamp are both newest, and neither is offered.</summary>
    [Fact]
    public void KeepsEveryPackageThatSharesTheNewestStamp()
    {
        var drivers = SupersededDrivers.Of(
            [Package("oem1.inf", "2023-01-01"), Package("oem2.inf", "2023-01-01"), Package("oem3.inf", "2020-01-01")],
            Store);

        Assert.Equal("oem3.inf", Assert.Single(drivers.Superseded).Package.PublishedName);
        Assert.Equal(["oem1.inf", "oem2.inf"], drivers.Newest.Select(p => p.PublishedName));
    }

    [Fact]
    public void ALonePackageIsNeverOffered()
    {
        var drivers = SupersededDrivers.Of([Package("oem1.inf", "2010-01-01")], Store);

        Assert.Empty(drivers.Superseded);
        Assert.Empty(drivers.Newest);
    }

    /// <summary>
    /// A package is of one kind with another only where its INF name, its provider, its device class and
    /// any extension it provides all match. A difference in any one keeps both.
    /// </summary>
    [Fact]
    public void PackagesThatDifferInAnyPartOfTheirKindAreNotCompared()
    {
        var older = Package("oem1.inf", "2020-01-01");

        Assert.Empty(Offered(older, Package("oem2.inf", "2024-01-01", original: "wifi2.inf")));
        Assert.Empty(Offered(older, Package("oem2.inf", "2024-01-01", provider: "Another Vendor")));
        Assert.Empty(Offered(older, Package("oem2.inf", "2024-01-01", classGuid: "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}")));
        Assert.Empty(Offered(
            Package("oem1.inf", "2020-01-01", extension: "{11111111-1111-1111-1111-111111111111}"),
            Package("oem2.inf", "2024-01-01", extension: "{22222222-2222-2222-2222-222222222222}")));
    }

    [Fact]
    public void ComparesNamesWithoutRegardToCase()
    {
        Assert.Equal(
            ["oem1.inf"],
            Offered(Package("oem1.inf", "2020-01-01"), Package("oem2.inf", "2024-01-01", original: "WIFI.INF", provider: "EXAMPLE NETWORKS")));
    }

    /// <summary>Windows refuses to remove a package a device uses, and Deguffer never asks it to.</summary>
    [Fact]
    public void NeverOffersAnOlderPackageADeviceIsUsing()
    {
        var drivers = SupersededDrivers.Of(
            [Package("oem1.inf", "2020-01-01", inUse: true), Package("oem2.inf", "2024-01-01")],
            Store);

        Assert.Empty(drivers.Superseded);
        Assert.Equal("oem1.inf", Assert.Single(drivers.InUse).PublishedName);
    }

    /// <summary>
    /// §5.2: only a child of the store. A package whose folder Windows did not name, or named anywhere
    /// else, including the store itself, is counted and left alone.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(Store)]
    [InlineData(@"C:\Windows\System32\DriverStore")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\wifi.inf_amd64_1\nested")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepositoryOld\wifi.inf_amd64_1")]
    public void NeverOffersAFolderThatIsNotAChildOfTheStore(string? folder)
    {
        var drivers = SupersededDrivers.Of(
            [Package("oem1.inf", "2020-01-01", folder: folder), Package("oem2.inf", "2024-01-01")],
            Store);

        Assert.Empty(drivers.Superseded);
        Assert.Equal(1, drivers.Unplaced);
    }

    /// <summary>A child of the store is recognised in either path form (§6.3).</summary>
    [Fact]
    public void RecognisesAChildGivenInTheExtendedForm()
    {
        Assert.True(SupersededDrivers.IsChildOf(LongPath.Extended(Path.Combine(Store, "wifi.inf_amd64_1")), Store));
        Assert.True(SupersededDrivers.IsChildOf(Path.Combine(Store, "wifi.inf_amd64_1") + @"\", Store));
    }
}
