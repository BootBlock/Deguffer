using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Settings page's bindable surface. It maps preferences to and from what the controls
/// actually expose — a combo box has a selected index, not an <see cref="AppTheme"/> — and says, on
/// the row that made it, when a change could not be written to disk.
///
/// The values themselves live in <see cref="PreferenceService"/>; this holds none of them.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PreferenceService _preferences;
    private readonly SourceRootService _sourceRoots;
    private readonly EmulatorFolderService _emulatorFolders;
    private readonly KeepService _keeps;
    private readonly SourceFolderAddition _sourceFolderAddition;
    private readonly LatestSave _latest;

    /// <param name="sourceFolderAddition">
    /// How a folder the user picks is approved, the same way the Storage page approves one. Built
    /// over <paramref name="sourceRoots"/>, so the folder it stores is the one this page lists.
    /// </param>
    /// <param name="latest">The page's one notice of a failed save, shared with every form on the page.</param>
    public SettingsViewModel(
        PreferenceService preferences,
        SourceRootService sourceRoots,
        EmulatorFolderService emulatorFolders,
        KeepService keeps,
        SourceFolderAddition sourceFolderAddition,
        LatestSave latest)
    {
        _preferences = preferences;
        _sourceRoots = sourceRoots;
        _emulatorFolders = emulatorFolders;
        _keeps = keeps;
        _sourceFolderAddition = sourceFolderAddition;
        _latest = latest;

        _announcedSpacing = preferences.Current.TreemapSpacing;
        SourceRoots = [.. sourceRoots.Current];
        EmulatorFolders = [.. emulatorFolders.Current];
        KeptItems = [.. InDisplayOrder(keeps.Current)];

        Defaults = new SettingsFormDefaults(preferences, this, Reset);
    }

    /// <summary>Each row's default, and the way back to it.</summary>
    public SettingsFormDefaults Defaults { get; }

    /// <summary>
    /// Put one setting back to its default. Every control reads the stored settings again, because the
    /// row that asked does not say which property its control is bound to.
    /// </summary>
    private void Reset(SettingSave row, SettingDefault setting) => Apply(row, setting.Reset, string.Empty);

    /// <summary>
    /// Read every control, the spacing box's record and every row's default again, after the page put
    /// back every setting at once.
    /// </summary>
    public void ShowStored()
    {
        _announcedSpacing = _preferences.Current.TreemapSpacing;
        OnPropertyChanged(string.Empty);
        Defaults.Follow();
    }

    /// <summary>
    /// Every item the user keeps, including ones no scan finds any more.
    ///
    /// <para>Listed in full because the keep list only ever narrows what Deguffer offers, and a
    /// narrowing nobody can see is how an item stays protected long after anyone remembers why. An
    /// entry whose item has gone costs nothing, and it is not dropped automatically: a cache on a drive
    /// that is not connected today is still kept tomorrow.</para>
    /// </summary>
    public ObservableCollection<KeptItem> KeptItems { get; }

    public bool HasNoKeptItems => KeptItems.Count == 0;

    /// <summary>
    /// Whether the saved keep list could not be read at startup. Stated on the page for as long as it
    /// is true, rather than as a failed save after a click, because it is a fact about a file the user
    /// has to repair or remove, and until they do, the list here is not the one they saved.
    /// </summary>
    public bool KeepListUnreadable => _keeps.StoredListUnreadable;

    /// <summary>Stop keeping <paramref name="item"/>, and re-read the list from the service.</summary>
    public void ReleaseKeptItem(KeptItem item)
    {
        // An unreadable stored list is already stated on the page, and the release writes nothing to it.
        _latest.Report(KeptItemsSave, _keeps.Release(item.ProviderId, item.Item.Key));

        // Brought up to date rather than emptied and filled: releasing one item costs the list one
        // removal, and the reader's place in the rest of it is where they left it.
        LiveList.Show(
            KeptItems, (KeptItem[])[.. InDisplayOrder(_keeps.Current)], kept => (kept.ProviderId, kept.Item.Key));

        OnPropertyChanged(nameof(HasNoKeptItems));
    }

    private static IEnumerable<KeptItem> InDisplayOrder(KeepList list) => list.Items
        .OrderBy(item => item.ProviderName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.Item.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// The folders Deguffer may look for build output in. Along with the guard on recently changed
    /// files, these change what gets deleted rather than how the window looks — and they are the
    /// pair that decides what Deguffer may even look at, which is why the page states where it will
    /// and will not look rather than presenting them as another preference.
    /// </summary>
    public ObservableCollection<SourceRoot> SourceRoots { get; }

    public bool HasNoSourceRoots => SourceRoots.Count == 0;

    /// <summary>
    /// Whether the last folder chosen for <see cref="SourceRoots"/> was not a folder on a disk. The
    /// row says so rather than the choice vanishing, because the picker let the user make it and
    /// nothing else on the page would explain why the list did not change.
    /// </summary>
    [ObservableProperty]
    public partial bool SourceRootNotOnDisk { get; set; }

    /// <summary>
    /// Ask for a folder and approve it. See <see cref="SourceFolderAddition.AddAsync"/> for what is
    /// asked, and why the approval goes through it rather than straight to the store.
    /// </summary>
    public async Task AddSourceRootAsync(CancellationToken ct)
    {
        var added = await _sourceFolderAddition.AddAsync(ct);

        if (added.WasChosen)
        {
            SourceRootNotOnDisk = added.Choice == SourceFolderChoice.NotOnDisk;
        }

        if (added.Choice == SourceFolderChoice.Approved)
        {
            Apply(added.Saved);
        }
    }

    public void RemoveSourceRoot(string root) => Apply(_sourceRoots.Remove(root));

    /// <summary>
    /// Report how a change to the folders was saved, and re-read the result from the service rather
    /// than assuming it took.
    ///
    /// The service adopts what the store actually kept, which is not always what was asked for, so
    /// mirroring the requested value here would let this list drift from the folders Deguffer will
    /// really search — the one place that drift is invisible to the user.
    /// </summary>
    private void Apply(SaveOutcome outcome)
    {
        _latest.Report(SourceRootsSave, outcome);

        LiveList.Show(SourceRoots, (SourceRoot[])[.. _sourceRoots.Current], root => root.Path);

        OnPropertyChanged(nameof(HasNoSourceRoots));
    }

    /// <summary>
    /// The folders the user has said an emulator is installed in. Like the source folders, they widen
    /// where Deguffer looks, so the page lists them in full.
    /// </summary>
    public ObservableCollection<string> EmulatorFolders { get; }

    public bool HasNoEmulatorFolders => EmulatorFolders.Count == 0;

    /// <param name="folder">What the picker returned, which may name nothing on a disk.</param>
    public void AddEmulatorFolder(string folder)
    {
        var onDisk = PickedFolder.OnDisk(folder);
        EmulatorFolderNotOnDisk = onDisk is null;

        if (onDisk is not null)
        {
            ApplyEmulatorFolders(() => _emulatorFolders.Add(onDisk));
        }
    }

    /// <summary>
    /// Whether the last folder chosen for <see cref="EmulatorFolders"/> was not a folder on a disk,
    /// for the reason <see cref="SourceRootNotOnDisk"/> gives.
    /// </summary>
    [ObservableProperty]
    public partial bool EmulatorFolderNotOnDisk { get; set; }

    public void RemoveEmulatorFolder(string folder) => ApplyEmulatorFolders(() => _emulatorFolders.Remove(folder));

    /// <summary>Run a change and re-read the list from the service, for the reason <see cref="Apply(SaveOutcome)"/> does.</summary>
    private void ApplyEmulatorFolders(Func<SaveOutcome> change)
    {
        _latest.Report(EmulatorFoldersSave, change());

        LiveList.Show(EmulatorFolders, (string[])[.. _emulatorFolders.Current], folder => folder);

        OnPropertyChanged(nameof(HasNoEmulatorFolders));
    }

    /// <summary>Index into the theme combo box, ordered to match <see cref="AppTheme"/>.</summary>
    public int ThemeIndex
    {
        get => (int)_preferences.Current.Theme;
        set => Apply(ThemeSave, current => current with { Theme = (AppTheme)value });
    }

    /// <summary>
    /// Index into the treemap spacing combo box, ordered to match <see cref="ExploreSpacing"/>.
    /// Presentation only: it moves the frames round the folders in a treemap and nothing else.
    /// </summary>
    public int TreemapSpacingIndex
    {
        get => (int)_preferences.Current.TreemapSpacing;
        set
        {
            // Recorded before the write, because the write calls FollowStoredSpacing as it lands and
            // Apply announces the box anyway. Read back after it, as a refused write stores nothing.
            _announcedSpacing = (ExploreSpacing)value;
            Apply(TreemapSpacingSave, current => current with { TreemapSpacing = (ExploreSpacing)value });
            _announcedSpacing = _preferences.Current.TreemapSpacing;
        }
    }

    /// <summary>
    /// The spacing <see cref="TreemapSpacingIndex"/> was last announced as, so a change to some other
    /// preference does not make the box read it again.
    /// </summary>
    private ExploreSpacing _announcedSpacing;

    /// <summary>
    /// Show a spacing chosen in the map appearance window, which writes the same setting while this page
    /// is open. The page calls this on every change to the preferences, so the spacing shown is one
    /// already in the file. No other setting here has a second place it is written.
    /// </summary>
    public void FollowStoredSpacing()
    {
        var stored = _preferences.Current.TreemapSpacing;

        if (stored == _announcedSpacing)
        {
            return;
        }

        _announcedSpacing = stored;
        OnPropertyChanged(nameof(TreemapSpacingIndex));
    }

    public bool BackdropEnabled
    {
        get => _preferences.Current.BackdropEnabled;
        set => Apply(BackdropEnabledSave, current => current with { BackdropEnabled = value });
    }

    /// <summary>
    /// Whether the Storage list draws a location with nothing left to reclaim. Presentation only,
    /// and it takes effect on the list that is already on screen — nothing is rescanned to hide or
    /// show a row.
    /// </summary>
    public bool ShowAlreadyClear
    {
        get => _preferences.Current.ShowAlreadyClear;
        set => Apply(ShowAlreadyClearSave, current => current with { ShowAlreadyClear = value });
    }

    /// <summary>
    /// Whether the Storage list scrolls to the row a clean is on and lights it up. Presentation only:
    /// the status line says what the run is doing either way.
    /// </summary>
    public bool SpotlightCleanedRow
    {
        get => _preferences.Current.SpotlightCleanedRow;
        set => Apply(SpotlightCleanedRowSave, current => current with { SpotlightCleanedRow = value });
    }

    public bool ConfirmBeforeCleaning
    {
        get => _preferences.Current.ConfirmBeforeCleaning;
        set => Apply(ConfirmBeforeCleaningSave, current => current with { ConfirmBeforeCleaning = value });
    }

    public bool RequireTypedConfirmation
    {
        get => _preferences.Current.RequireTypedConfirmation;
        set => Apply(RequireTypedConfirmationSave, current => current with { RequireTypedConfirmation = value });
    }

    /// <summary>
    /// How a Recycle Bin gets emptied. Off means Windows does it, which is the shipped route.
    ///
    /// <para>It is on this page rather than decided for the user because neither answer is right
    /// for everybody, and because the two costs are of different kinds: asking Windows keeps every
    /// window on the machine agreeing with the disk, and doing it ourselves is several times faster
    /// on a bin large enough to be worth emptying. Neither changes which directory is emptied.</para>
    /// </summary>
    public bool EmptyRecycleBinsDirectly
    {
        get => _preferences.Current.EmptyRecycleBinsDirectly;
        set => Apply(EmptyRecycleBinsDirectlySave, current => current with { EmptyRecycleBinsDirectly = value });
    }

    /// <summary>
    /// Bound by the control as well as used by the clamp, so the box and the value it produces cannot
    /// disagree — a number typed past the maximum is otherwise accepted by one and silently rewritten
    /// by the other. See <see cref="EnteredSetting.MaximumKeepHours"/>.
    /// </summary>
    public double MaximumKeepHours => EnteredSetting.MaximumKeepHours;

    /// <summary>
    /// The guard on recently changed files, in whole hours, as a <see cref="double"/> because that
    /// is what a <c>NumberBox</c> exposes. <see cref="EnteredSetting.KeepHours"/> turns what was typed
    /// into what is stored.
    /// </summary>
    public double KeepFilesChangedWithinHours
    {
        get => _preferences.Current.KeepFilesChangedWithinHours;
        set => Apply(KeepFilesChangedWithinHoursSave, current => current with { KeepFilesChangedWithinHours = EnteredSetting.KeepHours(value) });
    }

    /// <summary>
    /// The bounds on the File History retention age, read from the provider that clamps to them so
    /// the box and the value it produces cannot disagree.
    ///
    /// <para>The floor is a safety rule rather than a validation nicety: <c>FhManagew.exe -cleanup
    /// 0</c> discards every version of everything that has left the protection scope. See
    /// <see cref="FileHistoryProvider"/>.</para>
    /// </summary>
    public double MinimumFileHistoryRetentionDays => FileHistoryProvider.MinimumRetentionDays;

    public double MaximumFileHistoryRetentionDays => FileHistoryProvider.MaximumRetentionDays;

    /// <summary>
    /// How old a File History version has to be before Windows may discard it, on the same terms as
    /// <see cref="KeepFilesChangedWithinHours"/>. <see cref="EnteredSetting.FileHistoryRetentionDays"/>
    /// turns what was typed into what is stored.
    /// </summary>
    public double FileHistoryRetentionDays
    {
        get => _preferences.Current.FileHistoryRetentionDays;
        set => Apply(FileHistoryRetentionDaysSave, current => current with { FileHistoryRetentionDays = EnteredSetting.FileHistoryRetentionDays(value) });
    }

    /// <summary>
    /// The bounds on the temporary-file age, read from the provider that clamps to them so the box
    /// and the value it produces cannot disagree.
    /// </summary>
    public double MinimumTemporaryFileAgeDays => TempDirectoryProvider.MinimumStaleDays;

    public double MaximumTemporaryFileAgeDays => TempDirectoryProvider.MaximumStaleDays;

    /// <summary>
    /// How long something must sit untouched in a temporary folder before Deguffer offers it, on
    /// the same terms as the two boxes above. <see cref="EnteredSetting.TemporaryFileAgeDays"/> turns
    /// what was typed into what is stored, and holds the safety rule on zero.
    /// </summary>
    public double MinimumTemporaryFileAge
    {
        get => _preferences.Current.MinimumTemporaryFileAgeDays;
        set => Apply(MinimumTemporaryFileAgeSave, current => current with { MinimumTemporaryFileAgeDays = EnteredSetting.TemporaryFileAgeDays(value) });
    }

    /// <summary>Whether the last change made through each row could not be saved, for that row to say so.</summary>
    public SettingSave ThemeSave { get; } = new();

    public SettingSave TreemapSpacingSave { get; } = new();

    public SettingSave BackdropEnabledSave { get; } = new();

    public SettingSave ShowAlreadyClearSave { get; } = new();

    public SettingSave SpotlightCleanedRowSave { get; } = new();

    public SettingSave ConfirmBeforeCleaningSave { get; } = new();

    public SettingSave RequireTypedConfirmationSave { get; } = new();

    public SettingSave KeepFilesChangedWithinHoursSave { get; } = new();

    public SettingSave MinimumTemporaryFileAgeSave { get; } = new();

    public SettingSave FileHistoryRetentionDaysSave { get; } = new();

    public SettingSave SourceRootsSave { get; } = new();

    public SettingSave EmulatorFoldersSave { get; } = new();

    public SettingSave KeptItemsSave { get; } = new();

    public SettingSave EmptyRecycleBinsDirectlySave { get; } = new();

    /// <param name="row">The row whose control made the change, which says so if it was not saved.</param>
    /// <param name="setting">The property whose control made the change, which is the setter calling this.</param>
    private void Apply(SettingSave row, Func<AppPreferences, AppPreferences> change, [CallerMemberName] string setting = "")
    {
        var outcome = _latest.Report(row, _preferences.Update(change));

        // A rejected write changes nothing, so the control is now showing a value that is not in
        // effect. Re-reading every bound property puts it back to what actually holds, rather than
        // leaving a toggle that claims a setting the app is not honouring.
        //
        // A write that succeeds can store something other than what was typed, because a number goes
        // through EnteredSetting first: half an hour is stored as one. The control that made the
        // change reads it back, or it goes on showing a value that is not in effect.
        var announced = outcome is SaveOutcome.Refused ? string.Empty : setting;

        if (announced.Length == 0)
        {
            // Every control reads back, the spacing box with them, so the spacing it shows is the
            // stored one and need not be announced again when the page passes the write on.
            _announcedSpacing = _preferences.Current.TreemapSpacing;
        }

        OnPropertyChanged(announced);
        Defaults.Follow();
    }
}
