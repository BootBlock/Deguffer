using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's rows are reached through UI Automation by the provider's id rather than by
/// their wording or their place in the order, and the Scan drop-down's tick for the same location
/// has an id of its own: the drop-down opens over the list, so both are on screen at once.
/// </summary>
public class StorageAutomationIdTests
{
    [Fact]
    public void EachControlOfARowIsNamedByTheProvidersId()
    {
        var temp = new FakeCleanupProvider("temp") { Category = ProviderCategory.TemporaryFiles, Steps = [Rows.Folder("temp", 10)] };
        using var page = new StoragePage([temp]);
        page.Scan();

        var ids = page.Row("temp").AutomationIds;

        Assert.Equal("LocationToggle.temp", ids.Toggle);
        Assert.Equal("LocationItemsLink.temp", ids.ItemsLink);
        Assert.Equal("LocationInformationLink.temp", ids.InformationLink);
    }

    [Fact]
    public void TheDropDownsTicksAreNamedApartFromTheRows()
    {
        var temp = new FakeCleanupProvider("temp") { Category = ProviderCategory.TemporaryFiles, Steps = [Rows.Folder("temp", 10)] };
        using var page = new StoragePage([temp]);
        page.Scan();

        var group = Assert.Single(page.ViewModel.ScanChoice.Shown);
        var choice = Assert.Single(group);

        Assert.Equal("ScanChoiceToggle.temp", choice.ToggleAutomationId);
        Assert.Equal("ScanChoiceGroupToggle.TemporaryFiles", group.ToggleAutomationId);
    }
}
