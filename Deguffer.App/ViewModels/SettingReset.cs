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

        Statement = setting().Statement;
        CanReset = !setting().IsDefault(preferences.Current);
    }

    /// <summary>The default in words, as the row shows it: <c>Default: 7 days</c>.</summary>
    public string Statement { get; private set; }

    /// <summary>Whether the stored value is off the default, which is when the row offers its reset.</summary>
    public bool CanReset { get; private set; }

    /// <summary>The setting the row sets now.</summary>
    internal SettingDefault Setting => _setting();

    public void Reset() => _reset(Setting);

    /// <summary>
    /// Read the stored value again, after a write or a change of the setting the row sets, and tell the
    /// row only what changed: most writes leave most rows as they were.
    /// </summary>
    internal void Refresh()
    {
        var setting = Setting;

        if (setting.Statement != Statement)
        {
            Statement = setting.Statement;
            OnPropertyChanged(nameof(Statement));
        }

        var canReset = !setting.IsDefault(_preferences.Current);

        if (canReset != CanReset)
        {
            CanReset = canReset;
            OnPropertyChanged(nameof(CanReset));
        }
    }
}
