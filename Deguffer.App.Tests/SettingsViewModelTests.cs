using System.Collections.Specialized;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Settings page holds the stored settings and the controls together: a typed number goes
/// through the rule that stores it, a failed write says so and puts every control back, and the keep
/// list is shown in full and in order. What a typed number becomes is Core's, and proven in
/// <c>EnteredSettingTests</c>.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeVolumeInventory _volumes = new();

    public SettingsViewModelTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Settings => Path.Combine(_environment.LocalAppData, "Deguffer");

    private (SettingsViewModel Page, PreferenceService Preferences) Page(KeepService? keeps = null)
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));

        return (
            new SettingsViewModel(
                preferences,
                new SourceRootService(new SourceRootStore(_environment)),
                new EmulatorFolderService(new EmulatorFolderStore(_environment)),
                keeps ?? new KeepService(new KeepStore(_environment)),
                _volumes),
            preferences);
    }

    private static KeptItem Kept(string provider, string item) =>
        new($"{provider}-id", provider, new ItemIdentity($"{item}-key", item));

    /// <summary>
    /// The page stores what <see cref="EnteredSetting"/> makes of what was typed, for each box.
    /// Each value here is one a bare cast or rounding would store differently.
    /// </summary>
    [Fact]
    public void ANumberTypedIntoEachBoxIsStoredAsTheEntryRuleSays()
    {
        var (page, preferences) = Page();

        page.MinimumTemporaryFileAge = 0.25;
        page.KeepFilesChangedWithinHours = 0.25;
        page.FileHistoryRetentionDays = 30;
        page.FileHistoryRetentionDays = double.NaN;

        Assert.Equal(1, preferences.Current.MinimumTemporaryFileAgeDays);
        Assert.Equal(1, preferences.Current.KeepFilesChangedWithinHours);
        Assert.Equal(AppPreferences.Default.FileHistoryRetentionDays, preferences.Current.FileHistoryRetentionDays);
        Assert.False(page.SaveFailed);
    }

    /// <summary>The box's own maximum is the rule's, so the box and the value it produces cannot disagree.</summary>
    [Fact]
    public void TheBoxesAreBoundedAsTheValuesTheyProduceAre()
    {
        var (page, preferences) = Page();

        page.KeepFilesChangedWithinHours = page.MaximumKeepHours + 50;

        Assert.Equal(page.MaximumKeepHours, preferences.Current.KeepFilesChangedWithinHours);
        Assert.Equal(EnteredSetting.MaximumKeepHours, page.MaximumKeepHours);
    }

    /// <summary>
    /// A rejected write changes nothing, so the control is showing a value that is not in effect. Every
    /// binding is read again, and reads what actually holds.
    /// </summary>
    [Fact]
    public void AWriteThatFailsSaysSoAndPutsEveryControlBack()
    {
        var (page, _) = Page();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        // A folder where the file goes, so the write is refused as a locked profile refuses it.
        Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        page.BackdropEnabled = false;

        Assert.True(page.SaveFailed);
        Assert.True(page.BackdropEnabled);
        Assert.Contains(string.Empty, raised);
    }

    /// <summary>
    /// A number goes through <see cref="EnteredSetting"/> before it is stored, so what holds can differ
    /// from what was typed. The box that made the change reads it back, or it goes on showing half an
    /// hour while the guard holds one.
    /// </summary>
    [Fact]
    public void ABoxReadsBackWhatWasStoredRatherThanWhatWasTyped()
    {
        var (page, _) = Page();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.KeepFilesChangedWithinHours = 0.5;

        Assert.Contains(nameof(SettingsViewModel.KeepFilesChangedWithinHours), raised);
        Assert.Equal(1, page.KeepFilesChangedWithinHours);
    }

    /// <summary>A write that succeeds is read back by the control that made it, and by no other.</summary>
    [Fact]
    public void AWriteThatSucceedsLeavesTheOtherControlsAlone()
    {
        var (page, preferences) = Page();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.BackdropEnabled = false;

        Assert.False(page.SaveFailed);
        Assert.False(preferences.Current.BackdropEnabled);
        Assert.Equal([nameof(SettingsViewModel.BackdropEnabled)], raised);
    }

    /// <summary>
    /// Listed in full, by the provider's name and then the item's, whatever case each was written in,
    /// so a reader can find an entry that no scan finds any more.
    /// </summary>
    [Fact]
    public void EveryKeptItemIsListedByProviderThenItemWhateverTheCase()
    {
        var keeps = new KeepService(new KeepStore(_environment));
        keeps.Keep(Kept("npm", "zeta"));
        keeps.Keep(Kept("Cargo", "beta"));
        keeps.Keep(Kept("npm", "Alpha"));
        keeps.Keep(Kept("cargo", "Alpha"));

        var (page, _) = Page(new KeepService(new KeepStore(_environment)));

        Assert.Equal(
            ["cargo/Alpha", "Cargo/beta", "npm/Alpha", "npm/zeta"],
            page.KeptItems.Select(kept => $"{kept.ProviderName}/{kept.Item.Name}"));
    }

    /// <summary>
    /// An unreadable keep list is stated on the page as a file to repair, not as a write that failed,
    /// and releasing an item from it costs the list one removal.
    /// </summary>
    [Fact]
    public void ReleasingFromAnUnreadableKeepListIsNotAFailedSave()
    {
        Directory.CreateDirectory(Settings);
        File.WriteAllText(Path.Combine(Settings, "keep.json"), "{ torn");

        var keeps = new KeepService(new KeepStore(_environment));
        var item = Kept("npm", "alpha");
        keeps.Keep(item);
        keeps.Keep(Kept("npm", "beta"));

        var (page, _) = Page(keeps);
        var told = new List<NotifyCollectionChangedAction>();
        page.KeptItems.CollectionChanged += (_, changed) => told.Add(changed.Action);

        page.ReleaseKeptItem(item);

        Assert.True(page.KeepListUnreadable);
        Assert.False(page.SaveFailed);
        Assert.Equal([NotifyCollectionChangedAction.Remove], told);
    }

    /// <summary>A keep list that could not be saved is a failed save, and the item stays kept.</summary>
    [Fact]
    public void ReleasingWhereTheWriteFailsSaysSoAndKeepsTheItem()
    {
        var keeps = new KeepService(new KeepStore(_environment));
        var item = Kept("npm", "alpha");
        keeps.Keep(item);

        var (page, _) = Page(keeps);
        Directory.CreateDirectory(Path.Combine(Settings, "keep.json.tmp"));

        page.ReleaseKeptItem(item);

        Assert.True(page.SaveFailed);
        Assert.Single(page.KeptItems);
    }

    /// <summary>
    /// A cloud client that mounted itself since the last plan would be missing from a remembered
    /// volume list, and the folder would be stored with no warning shown.
    /// </summary>
    [Fact]
    public void AskingAboutAFolderReadsTheDrivesAgain()
    {
        var (page, _) = Page();

        page.ApprovalFor(Path.Combine(_temp.Path, "src"));

        Assert.Equal(1, _volumes.InvalidateCount);
    }
}
