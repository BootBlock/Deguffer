using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// A setting for what a page draws is shown first and written second, so a choice takes effect for the
/// session even where the preferences file refuses it, and only that setting is written.
/// </summary>
public sealed class DrawnSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly PreferenceService _preferences;
    private readonly List<string> _told = [];

    public DrawnSettingTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _preferences = new PreferenceService(new PreferenceStore(_environment));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AChoiceIsShownBeforeItIsWritten()
    {
        var setting = Setting(shown => _told.Add($"shown {shown}, stored {_preferences.Current.ExploreNotesDismissed}"));

        Assert.True(setting.Choose(true));

        Assert.Equal(["shown True, stored False"], _told);
        Assert.True(_preferences.Current.ExploreNotesDismissed);
        Assert.True(new PreferenceStore(_environment).Load().ExploreNotesDismissed);
    }

    /// <summary>A reader who closes something over their picture does not find it still there because a file could not be written.</summary>
    [Fact]
    public void AChoiceTheFileRefusesIsStillShown()
    {
        var setting = Setting(shown => _told.Add($"shown {shown}"));
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));

        Assert.False(setting.Choose(true));

        Assert.Equal(["shown True"], _told);
        Assert.False(_preferences.Current.ExploreNotesDismissed);
    }

    [Fact]
    public void OnlyTheSettingChosenIsWritten()
    {
        Assert.True(_preferences.Update(current => current with { ExploreOverviewHidden = true }));
        var setting = Setting(_ => { });

        setting.Choose(true);

        var stored = new PreferenceStore(_environment).Load();
        Assert.True(stored.ExploreNotesDismissed);
        Assert.True(stored.ExploreOverviewHidden);
    }

    [Fact]
    public void TheStoredValueIsWhatThePreferencesHold()
    {
        var setting = Setting(_ => { });

        Assert.False(setting.Stored);

        Assert.True(_preferences.Update(current => current with { ExploreNotesDismissed = true }));

        Assert.True(setting.Stored);
    }

    private DrawnSetting<bool> Setting(Action<bool> show) => new(
        _preferences,
        current => current.ExploreNotesDismissed,
        (current, dismissed) => current with { ExploreNotesDismissed = dismissed },
        show);
}
