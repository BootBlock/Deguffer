using Deguffer.App.Shell;
using Windows.UI;

namespace Deguffer.App.Tests;

/// <summary>
/// Which parts of the system's look a new reading moved, which says what a control does about it: a
/// map restyles its outlines for colours and effects, and draws again for a text size or a change of
/// high contrast theme.
/// </summary>
public sealed class SystemLookStateTests
{
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Yellow = Color.FromArgb(255, 255, 255, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 120, 215);

    private static readonly ContrastTheme Aquatic = new(Black, Yellow, Blue, Yellow);

    private static readonly SystemLookState Before = new(null, Blue, 1, true);

    [Fact]
    public void An_identical_reading_moved_nothing()
    {
        Assert.Equal(SystemLookChange.None, Before.ChangesTo(Before with { }));
    }

    [Fact]
    public void High_contrast_turned_on_moved_the_colours_alone()
    {
        Assert.Equal(SystemLookChange.Colours, Before.ChangesTo(Before with { Contrast = Aquatic }));
    }

    [Fact]
    public void Another_high_contrast_theme_moved_the_colours()
    {
        var aquatic = Before with { Contrast = Aquatic };

        Assert.Equal(SystemLookChange.Colours, aquatic.ChangesTo(aquatic with { Contrast = Aquatic with { Hotlight = Blue } }));
    }

    [Fact]
    public void A_new_accent_moved_the_colours_alone()
    {
        Assert.Equal(SystemLookChange.Colours, Before.ChangesTo(Before with { Accent = Yellow }));
    }

    [Fact]
    public void A_new_text_size_moved_the_text_size_alone()
    {
        Assert.Equal(SystemLookChange.TextScale, Before.ChangesTo(Before with { TextScale = 1.5 }));
    }

    [Fact]
    public void Effects_turning_slow_moved_the_effects_alone()
    {
        Assert.Equal(SystemLookChange.Effects, Before.ChangesTo(Before with { EffectsFast = false }));
    }

    [Fact]
    public void Every_part_that_moved_is_named()
    {
        var after = new SystemLookState(Aquatic, Blue, 2, false);

        Assert.Equal(
            SystemLookChange.Colours | SystemLookChange.TextScale | SystemLookChange.Effects,
            Before.ChangesTo(after));
    }

    [Fact]
    public void The_map_is_drawn_in_the_themes_window_and_text_colours()
    {
        var map = Aquatic.ToMap();

        Assert.Equal((0, 0, 0), (map.Window.Red, map.Window.Green, map.Window.Blue));
        Assert.Equal((255, 255, 0), (map.Text.Red, map.Text.Green, map.Text.Blue));
    }

    [Fact]
    public void The_outlines_are_drawn_in_the_themes_window_highlight_and_hotlight_colours()
    {
        var theme = new ContrastTheme(Black, Yellow, Blue, Color.FromArgb(255, 1, 2, 3));

        Assert.Equal(new(Black, Blue, Color.FromArgb(255, 1, 2, 3)), theme.ToHighlight());
    }
}
