using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Viewing;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The rail folds and opens as the window crosses the width, and the reader's choice survives every
/// crossing. The rail reports its own folds as it reports the toggle's, so each test's rail tells the
/// fold back to it inside the assignment, as the navigation view's property callback does.
/// </summary>
public sealed class RailFoldingTests : IDisposable
{
    private const double Narrow = WindowSizing.MinimumWidth;
    private const double Wide = 1200;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly PreferenceService _preferences;
    private readonly List<bool> _shown = [];

    public RailFoldingTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _preferences = new PreferenceService(new PreferenceStore(_environment));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Half a 1920 x 1080 display at 150% is 640 effective pixels, and the window has to fit it to snap there.</summary>
    [Fact]
    public void TheWindowFitsHalfACommonLaptopScreen() =>
        Assert.True(WindowSizing.MinimumWidth <= 640);

    /// <summary>The narrowest window folds the rail, or the page at that width loses 184 pixels to labels.</summary>
    [Fact]
    public void TheNarrowestWindowFoldsTheRail() =>
        Assert.True(WindowSizing.MinimumWidth < RailFold.OpenWidth);

    [Fact]
    public void TheRailFoldsAndOpensAsTheWindowCrossesTheWidth()
    {
        var rail = Rail();

        rail.Fit(Wide);
        rail.Fit(Narrow);
        rail.Fit(Wide);

        Assert.Equal([true, false, true], _shown);
    }

    /// <summary>
    /// Each choice starting on either side of the width, because a choice is only at risk where the rail
    /// first shows it on the side the width would not: an open rail on a wide window reads back as a
    /// rail the width opened.
    /// </summary>
    [Theory]
    [InlineData(RailChoice.Automatic, Narrow)]
    [InlineData(RailChoice.Automatic, Wide)]
    [InlineData(RailChoice.Open, Narrow)]
    [InlineData(RailChoice.Open, Wide)]
    [InlineData(RailChoice.Collapsed, Narrow)]
    [InlineData(RailChoice.Collapsed, Wide)]
    public void CrossingTheWidthDoesNotRewriteTheStoredChoice(RailChoice stored, double first)
    {
        Assert.True(_preferences.Update(current => current with { Rail = stored }));
        var written = File.GetLastWriteTimeUtc(PreferencesFile);
        var rail = Rail();
        var second = first == Narrow ? Wide : Narrow;

        rail.Fit(first);
        rail.Fit(second);
        rail.Fit(first);

        Assert.Equal(stored, _preferences.Current.Rail);
        Assert.Equal(stored, new PreferenceStore(_environment).Load().Rail);
        Assert.Equal(written, File.GetLastWriteTimeUtc(PreferencesFile));
    }

    [Fact]
    public void FoldingAWideWindowsRailKeepsItFoldedWhenTheWindowNarrowsAndWidens()
    {
        var rail = Rail();
        rail.Fit(Wide);

        rail.Changed(open: false);
        rail.Fit(Narrow);
        rail.Fit(Wide);

        Assert.Equal(RailChoice.Collapsed, new PreferenceStore(_environment).Load().Rail);
        Assert.Equal([true], _shown);
    }

    [Fact]
    public void OpeningANarrowWindowsRailKeepsItOpenWhenTheWindowWidensAndNarrows()
    {
        var rail = Rail();
        rail.Fit(Narrow);

        rail.Changed(open: true);
        rail.Fit(Wide);
        rail.Fit(Narrow);

        Assert.Equal(RailChoice.Open, new PreferenceStore(_environment).Load().Rail);
        Assert.Equal([false], _shown);
    }

    [Fact]
    public void FoldingTheRailBackOnANarrowWindowHandsItBackToTheWidth()
    {
        Assert.True(_preferences.Update(current => current with { Rail = RailChoice.Open }));
        var rail = Rail();
        rail.Fit(Narrow);

        rail.Changed(open: false);
        rail.Fit(Wide);

        Assert.Equal(RailChoice.Automatic, new PreferenceStore(_environment).Load().Rail);
        Assert.Equal([true, true], _shown);
    }

    private string PreferencesFile => Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json");

    private RailFolding Rail()
    {
        RailFolding? rail = null;
        rail = new RailFolding(_preferences, open =>
        {
            _shown.Add(open);
            rail!.Changed(open);
        });

        return rail;
    }
}
