using System.Xml.Linq;
using Deguffer.App.Shell;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// About credits every component a build redistributes. A package added to a shipping project with
/// no notice is one whose licence goes unmet, and a notice for a package no project references any
/// longer credits something the build does not carry, so the list is held to the project files.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    /// <summary>The projects whose output ships in a build.</summary>
    private static readonly string[] Shipping = ["Deguffer.App/Deguffer.App.csproj", "Deguffer.Core/Deguffer.Core.csproj"];

    /// <summary>
    /// The Windows SDK build tools generate the projection at build time and put nothing of their own
    /// in the output, so they need no notice.
    /// </summary>
    private static readonly string[] BuildOnly = ["Microsoft.Windows.SDK.BuildTools"];

    [Fact]
    public void EveryPackageAShippingProjectReferencesIsCreditedAndNoOtherIs()
    {
        var referenced = Shipping
            .SelectMany(project => XDocument.Load(Path.Combine(SourceTree.RepositoryRoot, project))
                .Descendants("PackageReference")
                .Select(reference => (string?)reference.Attribute("Include")))
            .OfType<string>()
            .Except(BuildOnly, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            referenced,
            ThirdPartyNotices.Components.SelectMany(component => component.Packages).Order(StringComparer.Ordinal));
    }

    /// <summary>Each build is self-contained, so the runtime ships in it and is credited too.</summary>
    [Fact]
    public void TheDotNetRuntimeIsCredited() =>
        Assert.Contains(ThirdPartyNotices.Components, component => component.Name == ".NET" && component.Packages.Count == 0);

    [Fact]
    public void EveryLicenceIsReadOverHttps() =>
        Assert.All(ThirdPartyNotices.Components, component => Assert.Equal(Uri.UriSchemeHttps, component.LicenceUri.Scheme));

    [Fact]
    public void ALicenceLinkIsNamedForWhoseLicenceItIs() =>
        Assert.Equal(
            "MIT, the licence of CommunityToolkit.Mvvm",
            Assert.Single(ThirdPartyNotices.Components, component => component.Name == "CommunityToolkit.Mvvm").LicenceLinkName);
}
