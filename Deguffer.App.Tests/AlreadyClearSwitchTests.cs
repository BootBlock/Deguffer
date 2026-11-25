using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// The Storage page's "Show items that are already clear" box and the Settings page's switch are one
/// preference: each reads what the other wrote, and a write the file refuses takes effect in neither.
/// </summary>
public sealed class AlreadyClearSwitchTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();
    private readonly PreferenceService _preferences;

    public AlreadyClearSwitchTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _preferences = new PreferenceService(new PreferenceStore(_environment));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheBoxOnTheStoragePageWritesThePreferenceSettingsReads()
    {
        var box = new AlreadyClearSwitch(_preferences);
        var settings = Settings();

        box.IsOn = true;

        Assert.True(settings.ShowAlreadyClear);
        Assert.True(new PreferenceStore(_environment).Load().ShowAlreadyClear);
        Assert.False(box.Save.Failed);
    }

    /// <summary>The empty state's button ticks the box: it writes the same preference, and the box reads it.</summary>
    [Fact]
    public void TheEmptyStatesButtonTicksTheBox()
    {
        var box = new AlreadyClearSwitch(_preferences);
        var raised = new List<string?>();
        box.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        box.ShowCommand.Execute(null);

        Assert.True(box.IsOn);
        Assert.True(new PreferenceStore(_environment).Load().ShowAlreadyClear);
        Assert.Contains(nameof(AlreadyClearSwitch.IsOn), raised);
    }

    /// <summary>The page calls <see cref="AlreadyClearSwitch.Follow"/> when the preferences change, and the box then reads what Settings wrote.</summary>
    [Fact]
    public void TheBoxReadsWhatSettingsWrote()
    {
        var box = new AlreadyClearSwitch(_preferences);
        var raised = new List<string?>();
        box.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Settings().ShowAlreadyClear = true;
        box.Follow();

        Assert.True(box.IsOn);
        Assert.Equal([nameof(AlreadyClearSwitch.IsOn)], raised);
    }

    /// <summary>A write the file refuses leaves the filter off, puts the box back, and says so.</summary>
    [Fact]
    public void ARefusedWriteGoesBackAndSaysSo()
    {
        var box = new AlreadyClearSwitch(_preferences);
        var raised = new List<string?>();
        box.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));

        box.IsOn = true;

        Assert.False(box.IsOn);
        Assert.False(_preferences.Current.ShowAlreadyClear);
        Assert.True(box.Save.Failed);
        Assert.Equal([nameof(AlreadyClearSwitch.IsOn)], raised);
    }

    /// <summary>
    /// The box put back after a refused write sends the value already stored, which writes nothing,
    /// and the notice of the refusal stays open. A later write that saves closes it.
    /// </summary>
    [Fact]
    public void TheBoxPutBackAfterARefusalLeavesTheNoticeOpenUntilAWriteSaves()
    {
        var box = new AlreadyClearSwitch(_preferences);
        var blocker = Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json");
        Directory.CreateDirectory(blocker);

        box.IsOn = true;
        box.IsOn = false;

        Assert.True(box.Save.Failed);

        Directory.Delete(blocker);
        box.IsOn = true;

        Assert.False(box.Save.Failed);
        Assert.True(_preferences.Current.ShowAlreadyClear);
    }

    private SettingsViewModel Settings()
    {
        var roots = new SourceRootService(new SourceRootStore(_environment));

        return new SettingsViewModel(
            _preferences,
            roots,
            new EmulatorFolderService(new EmulatorFolderStore(_environment)),
            new KeepService(new KeepStore(_environment)),
            new SourceFolderAddition(roots, _volumes, () => new ScriptedSourceFolderPrompt()),
            new LatestSave());
    }
}
