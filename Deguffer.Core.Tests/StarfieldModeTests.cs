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
        Assert.Equal(StarfieldMode.Flying, StarfieldModes.For(On, highContrast: false, effectsFast: true, shown: true, covered: false));

    /// <summary>A high contrast theme has no starfield, whatever else holds.</summary>
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, true)]
    public void HighContrastHasNoStarfield(bool animationsEnabled, bool effectsFast, bool shown, bool covered) =>
        Assert.Equal(
            StarfieldMode.Absent,
            StarfieldModes.For(MotionToken.Starfield.For(animationsEnabled), highContrast: true, effectsFast, shown, covered));

    /// <summary>With animation effects off the stars are a still field, seen or not.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WithMotionOffTheFieldIsStill(bool shown, bool covered) =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(Off, highContrast: false, effectsFast: true, shown, covered));

    /// <summary>Over Remote Desktop, where effects are not fast, the field stands still rather than streaming frames.</summary>
    [Fact]
    public void WhereEffectsAreSlowTheFieldIsStill() =>
        Assert.Equal(StarfieldMode.Still, StarfieldModes.For(On, highContrast: false, effectsFast: false, shown: true, covered: false));

    /// <summary>A page that has gone, or a minimised or hidden window, stops the flight where it is.</summary>
    [Fact]
    public void NotShownTheFlightPauses() =>
        Assert.Equal(StarfieldMode.Paused, StarfieldModes.For(On, highContrast: false, effectsFast: true, shown: false, covered: false));

    /// <summary>
    /// A window covered by others is shown as far as the XAML host knows, and the flight pauses all the
    /// same, because nobody can see it.
    /// </summary>
    [Fact]
    public void CoveredTheFlightPauses() =>
        Assert.Equal(StarfieldMode.Paused, StarfieldModes.For(On, highContrast: false, effectsFast: true, shown: true, covered: true));
}
