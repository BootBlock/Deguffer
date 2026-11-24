using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Settings page's restore holds the stored settings and its button together: it is offered
/// while any setting is off its default, writes every default in one write and nothing but the
/// preferences, says at the foot when that write failed, and tells its button only when the answer
/// turns over. Which settings it puts back, and the question asked first, are Core's, and proven in
/// <c>ShippedSettingsTests</c> and <c>RestoreDefaultsPromptTests</c>.
/// </summary>
public sealed class SettingsRestoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public SettingsRestoreTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Settings => Path.Combine(_environment.LocalAppData, "Deguffer");

    private (SettingsRestore Restore, PreferenceService Preferences, LatestSave Latest) Restore()
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));
        var latest = new LatestSave();

        return (new SettingsRestore(preferences, latest), preferences, latest);
    }

    [Fact]
    public void NothingIsOfferedWhereEverySettingIsOnItsDefault()
    {
        var (restore, _, _) = Restore();

        Assert.False(restore.CanRestore);
        Assert.Null(restore.Prompt());
    }

    /// <summary>
    /// The restore writes every default the page shows, the scanning values included, and leaves the
    /// lists the reader built, the keep list, the source folders and the emulator folders, as their stores
    /// hold them.
    /// </summary>
    [Fact]
    public void RestoringWritesEveryDefaultAndLeavesTheKeepListAndTheFolders()
    {
        var source = Path.Combine(_temp.Path, "src");
        var emulator = Path.Combine(_temp.Path, "Cemu");
        new KeepService(new KeepStore(_environment)).Keep(new KeptItem("npm-id", "npm", new ItemIdentity("alpha-key", "alpha")));
        new SourceRootService(new SourceRootStore(_environment)).Add(new SourceRoot(source));
        new EmulatorFolderService(new EmulatorFolderStore(_environment)).Add(emulator);

        var (restore, preferences, _) = Restore();
        preferences.Update(current => current with
        {
            Theme = AppTheme.Dark,
            ConfirmBeforeCleaning = false,
            Scanning = current.Scanning.With(StorageMedia.Rotational, new MediaScanPreferences(ListingBufferKiB: 64)),
        });
        restore.Follow();

        Assert.True(restore.CanRestore);
        Assert.Equal(3, restore.Prompt()?.Items.Count);

        restore.Restore();

        Assert.Empty(ShippedSettings.Changed(preferences.Current));
        Assert.Empty(ShippedSettings.Changed(new PreferenceStore(_environment).Load()));
        Assert.False(restore.CanRestore);
        Assert.False(restore.Save.Failed);
        Assert.Equal(["alpha"], new KeepService(new KeepStore(_environment)).Current.Items.Select(kept => kept.Item.Name));
        Assert.Equal([source], new SourceRootService(new SourceRootStore(_environment)).Current.Select(root => root.Path));
        Assert.Equal([emulator], new EmulatorFolderService(new EmulatorFolderStore(_environment)).Current);
    }

    /// <summary>A restore that could not be written says so at the foot and changes nothing, so the button is still offered.</summary>
    [Fact]
    public void ARestoreThatFailsSaysSoAndChangesNothing()
    {
        var (restore, preferences, _) = Restore();
        preferences.Update(current => current with { KeepFilesChangedWithinHours = 8 });
        File.Delete(Path.Combine(Settings, "preferences.json"));
        Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        restore.Restore();

        Assert.True(restore.Save.Failed);
        Assert.Equal(8, preferences.Current.KeepFilesChangedWithinHours);
        Assert.True(restore.CanRestore);
    }

    /// <summary>
    /// The page passes on every write, so the button is told when the answer turns over and not on each
    /// one: a second setting moved off its default leaves it offered, and says nothing.
    /// </summary>
    [Fact]
    public void TheButtonIsToldOnlyWhenItsAnswerTurnsOver()
    {
        var (restore, preferences, _) = Restore();
        var raised = new List<string?>();
        restore.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        preferences.Update(current => current with { BackdropEnabled = false });
        restore.Follow();
        preferences.Update(current => current with { ConfirmBeforeCleaning = false });
        restore.Follow();
        restore.Follow();

        Assert.Equal([nameof(SettingsRestore.CanRestore)], raised);
        Assert.True(restore.CanRestore);
    }
}
