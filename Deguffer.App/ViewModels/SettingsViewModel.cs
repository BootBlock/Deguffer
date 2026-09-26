using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Settings page's bindable surface. It maps preferences to and from what the controls
/// actually expose — a combo box has a selected index, not an <see cref="AppTheme"/> — and says so
/// when a change could not be written to disk.
///
/// The values themselves live in <see cref="PreferenceService"/>; this holds none of them.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PreferenceService _preferences;
    private readonly SourceRootService _sourceRoots;
    private readonly EmulatorFolderService _emulatorFolders;
    private readonly KeepService _keeps;
    private readonly IVolumeInventory _volumes;

    /// <param name="volumes">
    /// The machine's volumes, so that approving a folder can say what the folder is stored on.
    /// Required rather than defaulted, as <see cref="ExploreViewModel"/> takes it: the page passes
    /// the machine's own and a test passes a fake, and neither is the one to fall back to.
    /// </param>
    public SettingsViewModel(
        PreferenceService preferences,
        SourceRootService sourceRoots,
        EmulatorFolderService emulatorFolders,
        KeepService keeps,
        IVolumeInventory volumes)
    {
        _preferences = preferences;
        _sourceRoots = sourceRoots;
        _emulatorFolders = emulatorFolders;
        _keeps = keeps;
        _volumes = volumes;

        SourceRoots = [.. sourceRoots.Current];
        EmulatorFolders = [.. emulatorFolders.Current];
        KeptItems = [.. InDisplayOrder(keeps.Current)];
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
        // An unreadable stored list is already stated on the page, and is not a write that failed.
        SaveFailed = !_keeps.Release(item.ProviderId, item.Item.Key) && !_keeps.StoredListUnreadable;

        // Brought up to date rather than emptied and filled: releasing one item costs the list one
        // removal, and the reader's place in the rest of it is where they left it.
        LiveList.Show(
            KeptItems, [.. InDisplayOrder(_keeps.Current)], kept => (kept.ProviderId, kept.Item.Key));

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
    /// What the user has to be told before <paramref name="folder"/> is approved, if anything. The
    /// page shows it and asks; nothing is stored by asking.
    ///
    /// <para>The remembered volume list is dropped first. It is kept for the life of a planning pass,
    /// so a cloud client that mounted itself since the last one would be missing from it and the
    /// folder would be stored with no warning shown. One enumeration of the machine's drives per
    /// folder the user picks by hand is not a cost worth a stale answer.</para>
    /// </summary>
    public SourceRootApproval ApprovalFor(string folder)
    {
        _volumes.Invalidate();

        return SourceRootApproval.For(_volumes, folder);
    }

    /// <summary>
    /// Approve the folder <paramref name="approval"/> names, once the user has read whatever it had to
    /// say about it.
    ///
    /// <para>It takes the approval rather than a path, so that a folder cannot be stored as approved
    /// for a cloud mount unless the sentence explaining that was built for it — see
    /// <see cref="SourceRootApproval.Accepted"/>. Re-approving an already-approved folder is passed
    /// through rather than short-circuited here, because that is how a folder a plan refused comes to
    /// carry the approval.</para>
    /// </summary>
    public void AddSourceRoot(SourceRootApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);

        Apply(() => _sourceRoots.Add(approval.Accepted()));
    }

    public void RemoveSourceRoot(string root) => Apply(() => _sourceRoots.Remove(root));

    /// <summary>
    /// Run a change and re-read the result from the service rather than assuming it took.
    ///
    /// The service adopts what the store actually kept, which is not always what was asked for, so
    /// mirroring the requested value here would let this list drift from the folders Deguffer will
    /// really search — the one place that drift is invisible to the user.
    /// </summary>
    private void Apply(Func<bool> change)
    {
        SaveFailed = !change();

        LiveList.Show(SourceRoots, [.. _sourceRoots.Current], root => root.Path);

        OnPropertyChanged(nameof(HasNoSourceRoots));
    }

    /// <summary>
    /// The folders the user has said an emulator is installed in. Like the source folders, they widen
    /// where Deguffer looks, so the page lists them in full.
    /// </summary>
    public ObservableCollection<string> EmulatorFolders { get; }

    public bool HasNoEmulatorFolders => EmulatorFolders.Count == 0;

    public void AddEmulatorFolder(string folder) => ApplyEmulatorFolders(() => _emulatorFolders.Add(folder));

    public void RemoveEmulatorFolder(string folder) => ApplyEmulatorFolders(() => _emulatorFolders.Remove(folder));

    /// <summary>Run a change and re-read the list from the service, for the reason <see cref="Apply(Func{bool})"/> does.</summary>
    private void ApplyEmulatorFolders(Func<bool> change)
    {
        SaveFailed = !change();

        LiveList.Show(EmulatorFolders, [.. _emulatorFolders.Current], folder => folder);

        OnPropertyChanged(nameof(HasNoEmulatorFolders));
    }

    /// <summary>Index into the theme combo box, ordered to match <see cref="AppTheme"/>.</summary>
    public int ThemeIndex
    {
        get => (int)_preferences.Current.Theme;
        set => Apply(current => current with { Theme = (AppTheme)value });
    }

    /// <summary>
    /// Index into the treemap spacing combo box, ordered to match <see cref="ExploreSpacing"/>.
    /// Presentation only: it moves the frames round the folders in a treemap and nothing else.
    /// </summary>
    public int TreemapSpacingIndex
    {
        get => (int)_preferences.Current.TreemapSpacing;
        set => Apply(current => current with { TreemapSpacing = (ExploreSpacing)value });
    }

    public bool BackdropEnabled
    {
        get => _preferences.Current.BackdropEnabled;
        set => Apply(current => current with { BackdropEnabled = value });
    }

    /// <summary>
    /// Whether the Storage list draws a location with nothing left to reclaim. Presentation only,
    /// and it takes effect on the list that is already on screen — nothing is rescanned to hide or
    /// show a row.
    /// </summary>
    public bool ShowAlreadyClear
    {
        get => _preferences.Current.ShowAlreadyClear;
        set => Apply(current => current with { ShowAlreadyClear = value });
    }

    public bool ConfirmBeforeCleaning
    {
        get => _preferences.Current.ConfirmBeforeCleaning;
        set => Apply(current => current with { ConfirmBeforeCleaning = value });
    }

    public bool RequireTypedConfirmation
    {
        get => _preferences.Current.RequireTypedConfirmation;
        set => Apply(current => current with { RequireTypedConfirmation = value });
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
        set => Apply(current => current with { EmptyRecycleBinsDirectly = value });
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
        set => Apply(current => current with { KeepFilesChangedWithinHours = EnteredSetting.KeepHours(value) });
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
        set => Apply(current => current with { FileHistoryRetentionDays = EnteredSetting.FileHistoryRetentionDays(value) });
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
        set => Apply(current => current with { MinimumTemporaryFileAgeDays = EnteredSetting.TemporaryFileAgeDays(value) });
    }

    /// <summary>
    /// Shown only when a write failed. A settings page that silently discards a choice is worse
    /// than one that never offered it — the user has no way to tell it did not take.
    /// </summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; set; }

    private void Apply(Func<AppPreferences, AppPreferences> change)
    {
        SaveFailed = !_preferences.Update(change);

        // A rejected write changes nothing, so the control is now showing a value that is not in
        // effect. Re-reading every bound property puts it back to what actually holds, rather than
        // leaving a toggle that claims a setting the app is not honouring.
        if (SaveFailed)
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
