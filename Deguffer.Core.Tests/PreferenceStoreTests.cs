using Deguffer.Core.Configuration;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The settings file is hand-editable and survives upgrades, so the cases that matter are the
/// damaged ones. A preference that cannot be read must degrade to the default rather than stop
/// the app starting — nothing here is worth failing a launch over.
/// </summary>
public class PreferenceStoreTests
{
    [Fact]
    public void RoundTripsEveryPreference()
    {
        using var temp = new TempDirectory();
        var store = new PreferenceStore(new FakeUserEnvironment(temp.Path));

        Assert.True(store.Save(new AppPreferences(
            AppTheme.Dark,
            ViewDensity.Standard,
            ExploreView.List,
            ExploreColouring.Age,
            ShowNotInstalled: true,
            ShowAlreadyClear: true,
            BackdropEnabled: false,
            ConfirmBeforeCleaning: false,
            RequireTypedConfirmation: true,
            KeepFilesChangedWithinHours: 8)));

        var loaded = store.Load();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(ViewDensity.Standard, loaded.View);

        // Not the default, deliberately: Treemap is zero, so asserting the default here would pass
        // just as well with the preference deleted from the record altogether.
        Assert.Equal(ExploreView.List, loaded.Explore);

        // Not the default either, and for the same reason: Branch is zero.
        Assert.Equal(ExploreColouring.Age, loaded.ExploreColours);
        Assert.True(loaded.ShowNotInstalled);
        Assert.True(loaded.ShowAlreadyClear);
        Assert.False(loaded.BackdropEnabled);
        Assert.False(loaded.ConfirmBeforeCleaning);
        Assert.True(loaded.RequireTypedConfirmation);

        // Not the default, for the reason the two above give: zero is what the record declares and
        // what a missing key deserialises to, so asserting zero would pass with the preference
        // deleted outright.
        Assert.Equal(8, loaded.KeepFilesChangedWithinHours);
    }

    /// <summary>
    /// A settings file written before a preference existed is the ordinary case on upgrade, and the
    /// absent key must land on that preference's declared default rather than on whatever the JSON
    /// reader picks for a missing value.
    ///
    /// <c>ConfirmBeforeCleaning</c> and <c>View</c> are what prove it here. Their declared defaults,
    /// <c>true</c> and <c>Compact</c>, differ from <c>default(bool)</c> and
    /// <c>default(ViewDensity)</c>, so the two answers differ and the assertions have something to
    /// catch. Asserting the same thing about <c>RequireTypedConfirmation</c>,
    /// <c>ShowNotInstalled</c> or <c>ShowAlreadyClear</c> would prove nothing: for all three, both
    /// answers are <c>false</c>.
    ///
    /// <c>BackdropEnabled</c> could prove it too, and is spent instead as the parse guard below.
    /// One preference has to be a key the file actually carries, or a wholesale fall through to the
    /// defaults would satisfy every assertion here for the wrong reason.
    ///
    /// <c>ConfirmBeforeCleaning</c> is also the case with the consequence — an upgraded file read
    /// as "do not confirm" would silently drop the only question a Tier 3 row gets once the typed
    /// phrase is off.
    /// </summary>
    [Fact]
    public void AFileFromBeforeAPreferenceExistedTakesThatPreferencesDefault()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var directory = Directory.CreateDirectory(Path.Combine(environment.LocalAppData, "Deguffer"));

        File.WriteAllText(
            Path.Combine(directory.FullName, "preferences.json"),
            """{ "Theme": "Dark", "BackdropEnabled": false }""");

        var loaded = new PreferenceStore(environment).Load();

        // The file parsed, rather than falling through Load's catch to the defaults wholesale —
        // without which the two assertions below would hold for the wrong reason.
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.False(loaded.BackdropEnabled);

        Assert.True(loaded.ConfirmBeforeCleaning);
        Assert.Equal(ViewDensity.Compact, loaded.View);
        Assert.False(loaded.RequireTypedConfirmation);
        Assert.False(loaded.ShowNotInstalled);
        Assert.False(loaded.ShowAlreadyClear);

        // Off, which is the one direction this preference may default in: a guard nobody asked for
        // would quietly shrink every plan on an upgraded machine.
        Assert.Equal(0, loaded.KeepFilesChangedWithinHours);
    }

    /// <summary>
    /// The shipped answer to "what is asked before a deletion", on a machine nobody has configured.
    /// The blanket confirmation is on and the typed phrase is off, so a fresh install asks once, in
    /// words, naming everything it is about to remove. Either default flipping by accident changes
    /// that silently, which is why they are pinned rather than left to the record declaration.
    /// </summary>
    [Fact]
    public void TheShippedDefaultsAskOnceAndDoNotAskForTyping()
    {
        Assert.True(AppPreferences.Default.ConfirmBeforeCleaning);
        Assert.False(AppPreferences.Default.RequireTypedConfirmation);
    }

    /// <summary>
    /// What the Storage list looks like on a machine nobody has configured: one row per decision
    /// the user actually has to make, and nothing else. The three defaults are the same judgement
    /// reached from three directions — a location this machine does not have and a location with
    /// nothing left to reclaim both offer nothing to decide, and the rows that do are worth more of
    /// the screen than either.
    ///
    /// Neither filter is a claim that the hidden row is uninteresting. Each is still scanned, still
    /// counted, and one switch away from being listed.
    /// </summary>
    [Fact]
    public void TheShippedDefaultsListOnlyTheRowsThatCarryADecision()
    {
        Assert.Equal(ViewDensity.Compact, AppPreferences.Default.View);
        Assert.False(AppPreferences.Default.ShowNotInstalled);
        Assert.False(AppPreferences.Default.ShowAlreadyClear);
    }

    [Fact]
    public void UsesTheDefaultsOnFirstRun()
    {
        using var temp = new TempDirectory();

        var loaded = new PreferenceStore(new FakeUserEnvironment(temp.Path)).Load();

        Assert.Equal(AppPreferences.Default, loaded);
    }

    [Fact]
    public void FallsBackToTheDefaultsWhenTheFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new PreferenceStore(environment);
        store.Save(new AppPreferences(AppTheme.Dark, BackdropEnabled: false));

        File.WriteAllText(Path.Combine(environment.LocalAppData, "Deguffer", "preferences.json"), "{ not json");

        Assert.Equal(AppPreferences.Default, store.Load());
    }

    /// <summary>
    /// A theme name that no longer exists — an older or newer build's file. The unknown value must
    /// not take the rest of the settings with it, and must not throw.
    /// </summary>
    [Fact]
    public void FallsBackToTheDefaultsWhenAValueIsUnrecognised()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);
        var store = new PreferenceStore(environment);
        store.Save(AppPreferences.Default);

        File.WriteAllText(
            Path.Combine(environment.LocalAppData, "Deguffer", "preferences.json"),
            """{ "Theme": "Solarized", "BackdropEnabled": false }""");

        Assert.Equal(AppPreferences.Default, store.Load());
    }

    /// <summary>The directory does not exist until something is saved into it.</summary>
    [Fact]
    public void CreatesItsDirectoryOnFirstSave()
    {
        using var temp = new TempDirectory();
        var environment = new FakeUserEnvironment(temp.Path);

        Assert.True(new PreferenceStore(environment).Save(AppPreferences.Default));
        Assert.True(File.Exists(Path.Combine(environment.LocalAppData, "Deguffer", "preferences.json")));
    }
}
