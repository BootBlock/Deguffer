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
    /// The Storage list follows a clean unless the reader turns it off, and the switch stores the choice
    /// the page reads when it next applies its settings.
    /// </summary>
    [Fact]
    public void TheSwitchForFollowingACleanIsOnAndStoresItsChoice()
    {
        var (page, preferences) = Page();

        Assert.True(page.SpotlightCleanedRow);

        page.SpotlightCleanedRow = false;

        Assert.False(preferences.Current.SpotlightCleanedRow);
        Assert.False(new PreferenceStore(_environment).Load().SpotlightCleanedRow);
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

    /// <summary>
    /// A write that succeeds is read back by the control that made it, and by no other. The page's
    /// restore is the one other thing told, as a write can be what takes the last setting off its
    /// default or puts it back on.
    /// </summary>
    [Fact]
    public void AWriteThatSucceedsLeavesTheOtherControlsAlone()
    {
        var (page, preferences) = Page();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.BackdropEnabled = false;

        Assert.False(page.SaveFailed);
        Assert.False(preferences.Current.BackdropEnabled);
        Assert.Equal([nameof(SettingsViewModel.BackdropEnabled), nameof(SettingsViewModel.CanRestoreDefaults)], raised);
        Assert.True(page.CanRestoreDefaults);
    }

    /// <summary>
    /// The map appearance window writes the same spacing while the page is open, so the page's box reads
    /// it again rather than going on showing a spacing no longer in effect.
    /// </summary>
    [Fact]
    public void ASpacingChosenInTheMapAppearanceWindowIsShownInTheBox()
    {
        var (page, preferences) = Page();
        var window = new MapAppearanceViewModel(preferences);
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        window.SpacingIndex = (int)ExploreSpacing.Spacious;
        page.FollowStoredSpacing();

        Assert.Equal([nameof(SettingsViewModel.TreemapSpacingIndex)], raised);
        Assert.Equal((int)ExploreSpacing.Spacious, page.TreemapSpacingIndex);
    }

    /// <summary>
    /// The page hears of every change to the preferences. Only one that moved the spacing makes the box
    /// read it, including a move back to the spacing the page started with.
    /// </summary>
    [Fact]
    public void OnlyAChangeToTheSpacingMakesTheBoxReadIt()
    {
        var (page, preferences) = Page();
        var window = new MapAppearanceViewModel(preferences);
        page.TreemapSpacingIndex = (int)ExploreSpacing.Dense;
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        preferences.Update(current => current with { BackdropEnabled = false });
        page.FollowStoredSpacing();

        Assert.Empty(raised);

        window.SpacingIndex = (int)ExploreSpacing.Comfortable;
        page.FollowStoredSpacing();

        Assert.Equal([nameof(SettingsViewModel.TreemapSpacingIndex)], raised);
        Assert.Equal((int)ExploreSpacing.Comfortable, page.TreemapSpacingIndex);
    }

    /// <summary>
    /// The page hears of its own write as well, as the write lands, and the box reads the spacing once.
    /// The page's restore is told too, as the write takes the first setting off its default.
    /// </summary>
    [Fact]
    public void ASpacingChosenOnThePageIsAnnouncedOnce()
    {
        var (page, preferences) = Page();
        preferences.Changed += (_, _) => page.FollowStoredSpacing();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.TreemapSpacingIndex = (int)ExploreSpacing.Spacious;

        Assert.Equal([nameof(SettingsViewModel.TreemapSpacingIndex), nameof(SettingsViewModel.CanRestoreDefaults)], raised);
        Assert.Equal(ExploreSpacing.Spacious, preferences.Current.TreemapSpacing);
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
    /// volume list, and the folder would be stored with no warning shown. The drives are read off
    /// the window's thread, never by dropping the list for the window to read again.
    /// </summary>
    [Fact]
    public async Task AskingAboutAFolderReadsTheDrivesAgain()
    {
        var (page, _) = Page();

        await page.ApprovalForAsync(Path.Combine(_temp.Path, "src"), CancellationToken.None);

        Assert.Equal(1, _volumes.RefreshCount);
        Assert.Equal(0, _volumes.InvalidateCount);
    }

    /// <summary>
    /// A choice that is not a folder on a disk approves nothing, and the row says why until a folder
    /// on a disk is chosen.
    /// </summary>
    [Fact]
    public async Task ASourceFolderThatIsNotOnADiskIsNotApprovedAndTheRowSaysSo()
    {
        var (page, _) = Page();

        Assert.Null(await page.ApprovalForAsync(string.Empty, CancellationToken.None));
        Assert.True(page.SourceRootNotOnDisk);
        Assert.False(page.EmulatorFolderNotOnDisk);

        var folder = Path.Combine(_temp.Path, "src");

        Assert.Equal(folder, (await page.ApprovalForAsync(folder, CancellationToken.None))?.Path);
        Assert.False(page.SourceRootNotOnDisk);
    }

    /// <summary>The emulator folders answer the same choice the same way.</summary>
    [Fact]
    public void AnEmulatorFolderThatIsNotOnADiskIsNotAddedAndTheRowSaysSo()
    {
        var (page, _) = Page();

        page.AddEmulatorFolder(string.Empty);

        Assert.Empty(page.EmulatorFolders);
        Assert.True(page.EmulatorFolderNotOnDisk);
        Assert.False(page.SourceRootNotOnDisk);

        var folder = Path.Combine(_temp.Path, "Cemu");
        page.AddEmulatorFolder(folder);

        Assert.Equal([folder], page.EmulatorFolders);
        Assert.False(page.EmulatorFolderNotOnDisk);
    }

    /// <summary>
    /// A row's reset writes that setting's default, leaves every other setting where the reader put it,
    /// and stops offering itself, while the row still off its default goes on offering its own.
    /// </summary>
    [Fact]
    public void ResettingOneRowWritesItsDefaultAndLeavesTheOthersAlone()
    {
        var (page, preferences) = Page();
        page.KeepFilesChangedWithinHours = 8;
        page.MinimumTemporaryFileAge = 3;

        Assert.True(page.KeepFilesChangedWithinHoursDefault.CanReset);

        page.KeepFilesChangedWithinHoursDefault.Reset();

        Assert.Equal(AppPreferences.Default.KeepFilesChangedWithinHours, preferences.Current.KeepFilesChangedWithinHours);
        Assert.Equal(3, preferences.Current.MinimumTemporaryFileAgeDays);
        Assert.False(page.KeepFilesChangedWithinHoursDefault.CanReset);
        Assert.True(page.MinimumTemporaryFileAgeDefault.CanReset);
        Assert.Equal("Default: 0 hours", page.KeepFilesChangedWithinHoursDefault.Statement);
        Assert.False(page.SaveFailed);
    }

    /// <summary>
    /// A reset does not know which property its row's control is bound to, so every control reads back
    /// what was stored, and the row's own default reads again so its reset is withdrawn.
    /// </summary>
    [Fact]
    public void AResetHasEveryControlAndTheRowReadBack()
    {
        var (page, _) = Page();
        page.BackdropEnabled = false;
        var raised = new List<string?>();
        var row = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);
        page.BackdropEnabledDefault.PropertyChanged += (_, changed) => row.Add(changed.PropertyName);

        page.BackdropEnabledDefault.Reset();

        Assert.True(page.BackdropEnabled);
        Assert.Contains(string.Empty, raised);
        Assert.Equal([nameof(SettingReset.CanReset)], row);
        Assert.False(page.BackdropEnabledDefault.CanReset);
    }

    /// <summary>
    /// The restore button is told when the answer turns over and not on every write: a second setting
    /// moved off its default leaves it offered, and says nothing.
    /// </summary>
    [Fact]
    public void TheRestoreIsAnnouncedOnlyWhenItsAnswerChanges()
    {
        var (page, _) = Page();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.BackdropEnabled = false;
        page.ConfirmBeforeCleaning = false;
        page.FollowStoredDefaults();

        Assert.Single(raised, name => name == nameof(SettingsViewModel.CanRestoreDefaults));
        Assert.True(page.CanRestoreDefaults);
    }

    /// <summary>Each row's reset is the setting its name says, so no row puts back another's value.</summary>
    [Fact]
    public void EachRowsResetIsTheSettingItIsNamedFor()
    {
        var (page, _) = Page();

        foreach (var property in typeof(SettingsViewModel).GetProperties().Where(property => property.PropertyType == typeof(SettingReset)))
        {
            var name = property.Name[..^"Default".Length];
            var expected = typeof(ShippedSettings).GetField(name)?.GetValue(null);

            Assert.True(expected is not null, $"{property.Name} names no setting the page restores.");
            Assert.Same(expected, ((SettingReset)property.GetValue(page)!).Setting);
        }
    }

    /// <summary>
    /// Restoring every setting writes each one's default and leaves the lists the reader built, the keep
    /// list, the source folders and the emulator folders, as they were, on the page and in their stores.
    /// </summary>
    [Fact]
    public async Task RestoringAllLeavesTheKeepListAndTheFoldersUntouched()
    {
        var keeps = new KeepService(new KeepStore(_environment));
        keeps.Keep(Kept("npm", "alpha"));
        var (page, preferences) = Page(keeps);
        var source = Path.Combine(_temp.Path, "src");
        var emulator = Path.Combine(_temp.Path, "Cemu");
        page.AddSourceRoot((await page.ApprovalForAsync(source, CancellationToken.None))!);
        page.AddEmulatorFolder(emulator);
        page.ThemeIndex = (int)AppTheme.Dark;
        page.FileHistoryRetentionDays = 30;
        page.ConfirmBeforeCleaning = false;

        Assert.True(page.CanRestoreDefaults);
        Assert.Equal(3, page.RestorePrompt()?.Items.Count);

        page.RestoreDefaults();

        Assert.Empty(ShippedSettings.Changed(preferences.Current));
        Assert.False(page.CanRestoreDefaults);
        Assert.Null(page.RestorePrompt());
        Assert.False(page.SaveFailed);
        Assert.Equal([source], page.SourceRoots.Select(root => root.Path));
        Assert.Equal([emulator], page.EmulatorFolders);
        Assert.Equal(["alpha"], page.KeptItems.Select(kept => kept.Item.Name));
        Assert.Equal([source], new SourceRootService(new SourceRootStore(_environment)).Current.Select(root => root.Path));
        Assert.Equal([emulator], new EmulatorFolderService(new EmulatorFolderStore(_environment)).Current);
        Assert.Single(new KeepService(new KeepStore(_environment)).Current.Items);
    }

    /// <summary>A restore that could not be written says so, and every control reads what still holds.</summary>
    [Fact]
    public void ARestoreThatFailsSaysSoAndChangesNothing()
    {
        var (page, preferences) = Page();
        page.KeepFilesChangedWithinHours = 8;
        File.Delete(Path.Combine(Settings, "preferences.json"));
        Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        page.RestoreDefaults();

        Assert.True(page.SaveFailed);
        Assert.Equal(8, preferences.Current.KeepFilesChangedWithinHours);
        Assert.Equal(8, page.KeepFilesChangedWithinHours);
        Assert.True(page.CanRestoreDefaults);
    }
}
