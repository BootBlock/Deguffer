using System.Xml.Linq;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Explore page's Scan and Cancel share one slot, and Cancel takes Scan's place only while a
/// scan runs. These read the page's markup, because a binding to the wrong fact compiles and shows a
/// Cancel that cancels nothing: the page is busy during a removal as well, which has no Cancel.
/// </summary>
public sealed class ExploreScanSlotMarkupTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XName Group = XName.Get("WidthGroup.Group", "using:Deguffer.App.Controls");

    private static MarkupFile Page => Assert.Single(ShellMarkup.Files, file => file.Name == "Deguffer.App/Views/ExplorePage.xaml");

    [Fact]
    public void CancelTakesScansPlaceOnlyWhileAScanRuns()
    {
        Assert.Equal("{x:Bind ViewModel.ScanCommand.IsRunning, Mode=OneWay}", Named("CancelButton").Attribute("Visibility")?.Value);
        Assert.Equal(
            "{x:Bind controls:Shown.Unless(ViewModel.ScanCommand.IsRunning), Mode=OneWay}",
            Named("ScanButton").Attribute("Visibility")?.Value);
    }

    /// <summary>One slot, so the controls after it stay where they are as the two swap.</summary>
    [Fact]
    public void ScanAndCancelShareOneSlotOfOneWidth()
    {
        var scan = Named("ScanButton");
        var cancel = Named("CancelButton");

        Assert.Same(scan.Parent, cancel.Parent);
        Assert.Equal(scan.Attribute(Group)?.Value, cancel.Attribute(Group)?.Value);
        Assert.NotNull(scan.Attribute(Group));
    }

    private static XElement Named(string name) =>
        Assert.Single(Page.Elements, element => element.Attribute(Xaml + "Name")?.Value == name);
}
