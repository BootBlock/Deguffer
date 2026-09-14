using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
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
    private readonly KeepService _keeps;

    public SettingsViewModel(PreferenceService preferences, SourceRootService sourceRoots, KeepService keeps)
    {
        _preferences = preferences;
        _sourceRoots = sourceRoots;
        _keeps = keeps;

        SourceRoots = [.. sourceRoots.Current];
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
    public ObservableCollection<string> SourceRoots { get; }

    public bool HasNoSourceRoots => SourceRoots.Count == 0;

    /// <summary>Approve a folder. No-op if it was already approved.</summary>
    public void AddSourceRoot(string root)
    {
        if (SourceRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Apply(() => _sourceRoots.Add(root));
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

        LiveList.Show(SourceRoots, [.. _sourceRoots.Current], root => root);

        OnPropertyChanged(nameof(HasNoSourceRoots));
    }

    /// <summary>Index into the theme combo box, ordered to match <see cref="AppTheme"/>.</summary>
    public int ThemeIndex
    {
        get => (int)_preferences.Current.Theme;
        set => Apply(current => current with { Theme = (AppTheme)value });
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
    /// A week. Bound by the control as well as used by the clamp below, so the box and the value it
    /// produces cannot disagree — a number typed past the maximum is otherwise accepted by one and
    /// silently rewritten by the other.
    ///
    /// <para>A week rather than no limit at all: past that the guard stops being "leave what is in
    /// use alone" and becomes a second, invisible answer to what Deguffer will ever delete, which
    /// is a decision the row it sits on does not make.</para>
    /// </summary>
    public double MaximumKeepHours => 168;

    /// <summary>
    /// The guard on recently changed files, in whole hours, as a <see cref="double"/> because that
    /// is what a <c>NumberBox</c> exposes.
    ///
    /// <para>An emptied box reports <see cref="double.NaN"/> rather than zero, and NaN survives
    /// every comparison in <see cref="Math.Clamp(double, double, double)"/> — so it is answered
    /// first, as off. Without that, clearing the field would store NaN's cast, and the guard would
    /// be set to something nobody chose.</para>
    /// </summary>
    public double KeepFilesChangedWithinHours
    {
        get => _preferences.Current.KeepFilesChangedWithinHours;
        set => Apply(current => current with { KeepFilesChangedWithinHours = WholeHours(value) });
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
    /// <see cref="KeepFilesChangedWithinHours"/>: a <see cref="double"/> because that is what a
    /// <c>NumberBox</c> exposes, and an emptied box reports <see cref="double.NaN"/>.
    ///
    /// <para>NaN falls back to the shipped default rather than to the floor. Clearing the field is
    /// not a request to delete as much as possible, and the floor is the setting that destroys
    /// most.</para>
    /// </summary>
    public double FileHistoryRetentionDays
    {
        get => _preferences.Current.FileHistoryRetentionDays;
        set => Apply(current => current with { FileHistoryRetentionDays = WholeDays(value) });
    }

    /// <summary>
    /// The bounds on the temporary-file age, read from the provider that clamps to them so the box
    /// and the value it produces cannot disagree.
    /// </summary>
    public double MinimumTemporaryFileAgeDays => TempDirectoryProvider.MinimumStaleDays;

    public double MaximumTemporaryFileAgeDays => TempDirectoryProvider.MaximumStaleDays;

    /// <summary>
    /// How long something must sit untouched in a temporary folder before Deguffer offers it, on
    /// the same terms as the two boxes above: a <see cref="double"/> because that is what a
    /// <c>NumberBox</c> exposes, and an emptied box reports <see cref="double.NaN"/>.
    ///
    /// <para>NaN falls back to the shipped seven days rather than to the floor, for the reason
    /// <see cref="FileHistoryRetentionDays"/> gives and more sharply: clearing the field is not a
    /// request to delete everything in <c>%TEMP%</c> however recently it was written, and zero is
    /// the value that does exactly that.</para>
    /// </summary>
    public double MinimumTemporaryFileAge
    {
        get => _preferences.Current.MinimumTemporaryFileAgeDays;
        set => Apply(current => current with { MinimumTemporaryFileAgeDays = WholeStaleDays(value) });
    }

    /// <summary>
    /// <see cref="MidpointRounding.AwayFromZero"/> rather than the default, which is the one place
    /// on this page the difference decides a safety rule.
    ///
    /// <para>Zero here is not the smallest window but the absence of one, so every fraction of a
    /// day has to land on <c>1</c> rather than on it. Rounding alone does not do that, in either
    /// mode: <c>Math.Round</c> sends a midpoint to even so <c>0.5</c> becomes <b>0</b>, and
    /// <see cref="MidpointRounding.AwayFromZero"/> moves only the midpoint, leaving every value in
    /// <c>(0, 0.5)</c> on zero as well. Somebody typing <c>0.25</c> and meaning "a short window"
    /// would have stored the value that offers every file in both folders however recently it was
    /// written, without ever choosing it. So anything above zero and below a day is raised
    /// outright, and the rounding decides only between whole days above that.</para>
    ///
    /// <para><see cref="WholeDays"/> below does the same arithmetic and needs none of this: its
    /// clamp floor is one, so no rounding can reach a dangerous value there.</para>
    /// </summary>
    private int WholeStaleDays(double value)
    {
        if (double.IsNaN(value))
        {
            return AppPreferences.Default.MinimumTemporaryFileAgeDays;
        }

        // Zero itself is a deliberate choice and passes through. Anything between zero and a day is
        // somebody asking for a short window, and the shortest one that exists is a day.
        var days = value > 0 && value < 1 ? 1 : Math.Round(value, MidpointRounding.AwayFromZero);

        return (int)Math.Clamp(days, MinimumTemporaryFileAgeDays, MaximumTemporaryFileAgeDays);
    }

    private int WholeDays(double value) => double.IsNaN(value)
        ? AppPreferences.Default.FileHistoryRetentionDays
        : (int)Math.Clamp(
            Math.Round(value), MinimumFileHistoryRetentionDays, MaximumFileHistoryRetentionDays);

    private int WholeHours(double value) =>
        double.IsNaN(value) ? 0 : (int)Math.Clamp(Math.Round(value), 0, MaximumKeepHours);

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
