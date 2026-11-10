using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// When the About page's starfield flies, stands still, pauses or is not there: it moves only for a
/// reader who has animation on, on a compositor that draws it cheaply, while it can be seen.
/// </summary>
public sealed class StarfieldModeTests
{
    private static readonly Motion On = MotionToken.Starfield.For(animationsEnabled: true);
    private static readonly Motion Off = MotionToken.Starfield.For(animationsEnabled: false);

    [Fact]
    public void ItFliesWhenEverythingAllowsIt() =>
        Assert.Equal(StarfieldMode.Flying, StarfieldModes.For(On, highContrast: false, effectsFast: true, Sight.Seen));

    /// <summary>A high contrast theme has no starfield, whatever else holds.</summary>
    [Theory]
    [InlineData(true, true, Sight.Seen)]
    [InlineData(false, true, Sight.Seen)]
    [InlineData(true, false, Sight.Unseen)]
    [InlineData(true, true, Sight.Covered)]
    public void HighContrastHasNoStarfield(bool animationsEnabled, bool effectsFast, Sight sight) =>
        Assert.Equal(
            StarfieldMode.Absent,
            StarfieldModes.For(MotionToken.Starfield.For(animationsEnabled), highContrast: true, effectsFast, sight));

    /// <summary>With animation effects off the stars are a still field, seen or not.</summary>
    [Theory]
    [InlineData(Sight.Seen)]
    [InlineData(Sight.Unseen)]
    [InlineData(Sight.Covered)]
    public void WithMotionOffTheFieldIsStill(Sight sight) =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(Off, highContrast: false, effectsFast: true, sight));

    /// <summary>Over Remote Desktop, where effects are not fast, the field stands still rather than streaming frames.</summary>
    [Fact]
    public void WhereEffectsAreSlowTheFieldIsStill() =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(On, highContrast: false, effectsFast: false, Sight.Seen));

    /// <summary>
    /// A page that has gone, a minimised or hidden window, or one covered by others, which is shown as
    /// far as the XAML host knows, stops the flight where it is, because nobody can see it.
    /// </summary>
    [Theory]
    [InlineData(Sight.Unseen)]
    [InlineData(Sight.Covered)]
    public void UnseenTheFlightPauses(Sight sight) =>
        Assert.Equal(StarfieldMode.Paused, StarfieldModes.For(On, highContrast: false, effectsFast: true, sight));
}
