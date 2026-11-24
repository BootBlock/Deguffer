using Deguffer.App.ViewModels;

namespace Deguffer.App.Tests;

/// <summary>
/// The size history row's words come from its view-model, where the markup rule on a settings row's
/// description cannot read them, so the budget that rule holds (140 characters) is held here.
/// </summary>
public sealed class SizeHistorySettingsViewModelTests
{
    [Fact]
    public void TheRowIsDescribedInOneShortLine() =>
        Assert.InRange(SizeHistorySettingsViewModel.Description.Length, 1, 140);
}
