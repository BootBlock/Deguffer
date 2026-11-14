using Deguffer.App.Converters;
using Deguffer.Core.Startup;

namespace Deguffer.App.Tests;

/// <summary>
/// A Startup row is tinted by whether its entry starts, so the entries that start and the disabled
/// ones read apart down the list, and an entry whose state is unknown claims neither.
/// </summary>
public sealed class StartupRowStyleConverterTests
{
    [Theory]
    [InlineData(StartupState.Starts, "StartupRowStarts")]
    [InlineData(StartupState.Disabled, "StartupRowDisabled")]
    [InlineData(StartupState.Unknown, "StartupRowUnknown")]
    public void Each_state_has_its_own_tint(StartupState state, string style) =>
        Assert.Equal(style, StartupRowStyleConverter.KeyFor(state));

    [Fact]
    public void A_state_no_tint_is_named_for_is_left_untinted() =>
        Assert.Equal("StartupRowUnknown", StartupRowStyleConverter.KeyFor((StartupState)99));
}
