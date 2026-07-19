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

        Assert.True(store.Save(new AppPreferences(AppTheme.Dark, BackdropEnabled: false, ConfirmBeforeCleaning: false)));

        var loaded = store.Load();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.False(loaded.BackdropEnabled);
        Assert.False(loaded.ConfirmBeforeCleaning);
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
