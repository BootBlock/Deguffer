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
        Assert.Equal(StarfieldMode.Flying, StarfieldModes.For(On, highContrast: false, effectsFast: true, seen: true));

    /// <summary>A high contrast theme has no starfield, whatever else holds.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void HighContrastHasNoStarfield(bool animationsEnabled, bool effectsFast, bool seen) =>
        Assert.Equal(
            StarfieldMode.Absent,
            StarfieldModes.For(MotionToken.Starfield.For(animationsEnabled), highContrast: true, effectsFast, seen));

    /// <summary>With animation effects off the stars are a still field, seen or not.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithMotionOffTheFieldIsStill(bool seen) =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(Off, highContrast: false, effectsFast: true, seen));

    /// <summary>Over Remote Desktop, where effects are not fast, the field stands still rather than streaming frames.</summary>
    [Fact]
    public void WhereEffectsAreSlowTheFieldIsStill() =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(On, highContrast: false, effectsFast: false, seen: true));

    /// <summary>A page nobody can see, or a minimised window, stops the flight where it is.</summary>
    [Fact]
    public void UnseenTheFlightPauses() =>
        Assert.Equal(StarfieldMode.Paused, StarfieldModes.For(On, highContrast: false, effectsFast: true, seen: false));
}
