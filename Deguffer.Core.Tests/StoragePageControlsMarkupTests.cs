using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Storage page draws each of its controls where <c>StorageControls</c> says, and only there:
/// before a scan its header and its list carry nothing, and the empty card carries Scan. The
/// view-model's half of this is proven in <c>Deguffer.App.Tests</c>. These read the page's markup,
/// because a binding written to the wrong property, or a control left in the header, compiles and
/// draws the cluttered page the rule exists to prevent.
/// </summary>
public sealed class StoragePageControlsMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static MarkupFile Page => Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/Views/CleanPage.xaml");

    [Fact]
    public void TheListControlsAreDrawnOnlyOnceAScanHasProducedARow()
    {
        var bar = Named("ListControls");

        Assert.Equal("{x:Bind ViewModel.Controls.ListControls, Mode=OneWay}", bar.Attribute("Visibility")?.Value);
    }

    /// <summary>
    /// Both filters, the view, the order and the selection menu sit in the one bar over the list, so
    /// they are hidden with it and share a region with the rows they shape.
    /// </summary>
    [Theory]
    [InlineData("NotInstalledToggle")]
    [InlineData("AlreadyClearToggle")]
    [InlineData("ViewSelector")]
    [InlineData("OrderSelector")]
    public void EachListControlIsInTheBarOverTheList(string control)
    {
        Assert.Contains(Named("ListControls"), Named(control).Ancestors());
    }

    [Fact]
    public void TheSelectionMenuIsInTheBarOverTheList()
    {
        var menu = Assert.Single(Page.Elements, element => element.Name.LocalName == "StorageSelectionMenu");

        Assert.Contains(Named("ListControls"), menu.Ancestors());
    }

    /// <summary>The header holds what governs the whole page, and nothing on this page does, so before a scan it holds nothing.</summary>
    [Fact]
    public void ThePageHeaderHoldsNoControls()
    {
        var header = Assert.Single(Page.Elements, element => element.Name.LocalName == "PageHeader");

        Assert.Empty(header.Elements());
    }

    [Fact]
    public void TheEmptyCardCarriesScanWhereTheBarUnderTheListDoesNot()
    {
        var buttons = Named("EmptyScanButtons");

        Assert.Equal("{x:Bind ViewModel.Controls.EmptyStateOffersScan, Mode=OneWay}", buttons.Attribute("Visibility")?.Value);
        Assert.Equal("EmptyState.PrimaryAction", buttons.Parent?.Name.LocalName);
    }

    [Fact]
    public void TheBarUnderTheListIsDrawnOnlyWhereStorageControlsSays()
    {
        var bar = Named("BarScanButtons").Ancestors().First(element => element.Name.LocalName == "ReflowRow");

        Assert.Equal("{x:Bind ViewModel.Controls.ActionBar, Mode=OneWay}", bar.Attribute("Visibility")?.Value);
    }

    private static XElement Named(string name) =>
        Assert.Single(Page.Elements, element => element.Attribute(Xaml + "Name")?.Value == name);
}
