using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// A settings row's default: the line that states it, whether the row is off it, and the reset that puts
/// it back. What the default is, and what holds it, is Core's <see cref="SettingDefault"/>.
///
/// <para>The reset goes through the view-model that owns the row, because that view-model says when a
/// write failed and makes the row's control read back what was stored.</para>
/// </summary>
public sealed class SettingReset : ObservableObject
{
    private readonly PreferenceService _preferences;
    private readonly Func<SettingDefault> _setting;
    private readonly Action<SettingDefault> _reset;

    /// <param name="setting">
    /// The setting the row sets now. A function rather than a value, because a scan row sets the value
    /// of whichever kind of drive the picker names.
    /// </param>
    /// <param name="reset">The owning view-model's write of a setting's default.</param>
    internal SettingReset(PreferenceService preferences, Func<SettingDefault> setting, Action<SettingDefault> reset)
    {
        _preferences = preferences;
        _setting = setting;
        _reset = reset;
    }

    /// <summary>The default in words, as the row shows it: <c>Default: 7 days</c>.</summary>
    public string Statement => _setting().Statement;

    /// <summary>Whether the stored value is off the default, which is when the row offers its reset.</summary>
    public bool CanReset => !_setting().IsDefault(_preferences.Current);

    public void Reset() => _reset(_setting());

    /// <summary>Read the stored value again, after a write or a change of the setting the row sets.</summary>
    internal void Refresh() => OnPropertyChanged(string.Empty);
}
