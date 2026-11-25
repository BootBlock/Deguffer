using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Storage page's two row templates announce a row the same way, and the compact one opens its
/// detail from the row rather than from the control that drew it. These read the page's markup,
/// because a binding left off one density compiles and draws a row that looks right and says less.
/// What the checkbox's name says is the view-model's, proven in <c>Deguffer.App.Tests</c>.
/// </summary>
public sealed class StorageRowMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static MarkupFile Page => Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/Views/CleanPage.xaml");

    /// <summary>
    /// A keyboard or a screen reader stops on the checkbox and nowhere else on the row, so it carries
    /// the row's figure and tier in its name and §7's sentence as its help text.
    /// </summary>
    [Fact]
    public void TheRowCheckBoxCarriesTheFactsTheDecisionRestsOn()
    {
        var box = Assert.Single(Template("FindingCheckBox").Descendants(), element => element.Name.LocalName == "CheckBox");

        Assert.Equal("{x:Bind SelectionName, Mode=OneWay}", box.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("{x:Bind Facts.WhatHappensOnNextUse}", box.Attribute("AutomationProperties.HelpText")?.Value);
    }

    /// <summary>Both densities draw that one checkbox, so neither can carry less than the other.</summary>
    [Theory]
    [InlineData("StandardFinding")]
    [InlineData("CompactFinding")]
    public void EachRowTemplateDrawsTheOneRowCheckBox(string template)
    {
        var row = Template(template);

        Assert.DoesNotContain(row.Descendants(), element => element.Name.LocalName == "CheckBox");
        Assert.Single(row.Descendants(), element =>
            element.Attribute("ContentTemplate")?.Value == "{StaticResource FindingCheckBox}");
    }

    /// <summary>
    /// The compact row's detail opens from <c>IsDetailOpen</c>, both ways. The list recycles its
    /// containers, so a state held by the control would stay open on whichever row took it next.
    /// </summary>
    [Fact]
    public void TheCompactDisclosureFollowsTheRow()
    {
        var row = Template("CompactFinding");
        var chevron = Assert.Single(row.Descendants(), element => element.Name.LocalName == "DisclosureButton");

        Assert.Equal("{x:Bind IsDetailOpen, Mode=TwoWay}", chevron.Attribute("IsExpanded")?.Value);
        Assert.Single(row.Descendants(), element => element.Attribute("Visibility")?.Value == "{x:Bind IsDetailOpen, Mode=OneWay}");
    }

    private static XElement Template(string key) => Assert.Single(Page.Elements, element =>
        element.Name.LocalName == "DataTemplate" && element.Attribute(Xaml + "Key")?.Value == key);
}
