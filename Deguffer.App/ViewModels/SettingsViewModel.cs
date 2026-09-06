using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

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

    public SettingsViewModel(PreferenceService preferences, SourceRootService sourceRoots)
    {
        _preferences = preferences;
        _sourceRoots = sourceRoots;

        SourceRoots = [.. sourceRoots.Current];
    }

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

        SourceRoots.Clear();

        foreach (var root in _sourceRoots.Current)
        {
            SourceRoots.Add(root);
        }

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
