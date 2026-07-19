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
    /// The folders Deguffer may look for build output in. Unlike everything else on this page these
    /// change what gets deleted rather than how the window looks, which is why the page states where
    /// Deguffer will and will not look rather than presenting them as another preference.
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

    public bool ConfirmBeforeCleaning
    {
        get => _preferences.Current.ConfirmBeforeCleaning;
        set => Apply(current => current with { ConfirmBeforeCleaning = value });
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
