using System.Collections.Specialized;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Settings page holds the stored settings and the controls together: a typed number goes
/// through the rule that stores it, a failed write says so on the row that made it and puts every
/// control back, and the keep list is shown in full and in order. What a typed number becomes is Core's, and proven in
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
        Assert.Empty(FailedRows.Of(page));
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

        Assert.Equal([nameof(SettingsViewModel.BackdropEnabledSave)], FailedRows.Of(page));
        Assert.True(page.BackdropEnabled);
        Assert.Contains(string.Empty, raised);
    }

    /// <summary>
    /// The failure is stated on the row that made the change, so it is read where the value went back:
    /// a box several screens down the page, such as the File History retention, says so itself rather
    /// than through a notice scrolled out of view at the top of the page.
    /// </summary>
    [Fact]
    public void AWriteThatFailsIsStatedOnTheRowThatMadeItAndNoOther()
    {
        var (page, preferences) = Page();
        Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        page.FileHistoryRetentionDays = 30;

        Assert.Equal([nameof(SettingsViewModel.FileHistoryRetentionDaysSave)], FailedRows.Of(page));
        Assert.Equal(AppPreferences.Default.FileHistoryRetentionDays, preferences.Current.FileHistoryRetentionDays);
    }

    /// <summary>
    /// A box put back after a refused write sends the value it was put back to, which is the one
    /// already stored. That asks for nothing new and says nothing about the folder, so the row goes on
    /// saying the change was not saved, rather than closing the notice the moment it opened.
    /// </summary>
    [Fact]
    public void ABoxPutBackAfterARefusedWriteLeavesItsNoticeStanding()
    {
        var (page, _) = Page();
        Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        page.FileHistoryRetentionDays = 30;
        page.FileHistoryRetentionDays = page.FileHistoryRetentionDays;

        Assert.Equal([nameof(SettingsViewModel.FileHistoryRetentionDaysSave)], FailedRows.Of(page));
    }

    /// <summary>
    /// One failure is stated once: a later change that fails moves the notice to its own row, and a
    /// change that saves clears it, because the folder it tells the reader to check took a write.
    /// </summary>
    [Fact]
    public void TheNoticeFollowsTheLatestChangeAndASaveClearsIt()
    {
        var (page, _) = Page();
        var refused = Directory.CreateDirectory(Path.Combine(Settings, "preferences.json"));

        page.BackdropEnabled = false;
        page.KeepFilesChangedWithinHours = 4;

        Assert.Equal([nameof(SettingsViewModel.KeepFilesChangedWithinHoursSave)], FailedRows.Of(page));

        refused.Delete();
        page.ThemeIndex = 2;

        Assert.Empty(FailedRows.Of(page));
    }

    /// <summary>
    /// A list's add and remove buttons are on its row, so a list that could not be saved says so there,
    /// whichever list it was.
    /// </summary>
    [Fact]
    public void AListThatCouldNotBeSavedSaysSoOnItsOwnRow()
    {
        var (page, _) = Page();
        Directory.CreateDirectory(Path.Combine(Settings, "emulator-folders.json"));

        page.AddEmulatorFolder(Path.Combine(_temp.Path, "Cemu"));

        Assert.Equal([nameof(SettingsViewModel.EmulatorFoldersSave)], FailedRows.Of(page));
        Assert.Empty(page.EmulatorFolders);

        Directory.CreateDirectory(Path.Combine(Settings, "source-roots.json"));
        page.AddSourceRoot(SourceRootApproval.For(_volumes, Path.Combine(_temp.Path, "src")));

        Assert.Equal([nameof(SettingsViewModel.SourceRootsSave)], FailedRows.Of(page));
        Assert.Empty(page.SourceRoots);
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

        Assert.Empty(FailedRows.Of(page));
        Assert.False(preferences.Current.BackdropEnabled);
        Assert.Equal([nameof(SettingsViewModel.BackdropEnabled)], raised);
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
    /// </summary>
    [Fact]
    public void ASpacingChosenOnThePageIsAnnouncedOnce()
    {
        var (page, preferences) = Page();
        preferences.Changed += (_, _) => page.FollowStoredSpacing();
        var raised = new List<string?>();
        page.PropertyChanged += (_, changed) => raised.Add(changed.PropertyName);

        page.TreemapSpacingIndex = (int)ExploreSpacing.Spacious;

        Assert.Equal([nameof(SettingsViewModel.TreemapSpacingIndex)], raised);
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
        Assert.Empty(FailedRows.Of(page));
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

        Assert.Equal([nameof(SettingsViewModel.KeptItemsSave)], FailedRows.Of(page));
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
}
