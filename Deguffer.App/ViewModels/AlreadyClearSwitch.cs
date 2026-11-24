using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Storage page's "Show items that are already clear" filter. It reads and writes the one
/// preference the Settings page's switch of the same name does, so the two never disagree.
///
/// <para>Saved first and shown second, as Settings saves it, rather than shown first as the page's
/// not-installed filter is (<see cref="DrawnSetting{T}"/>). That filter has no other writer. This one
/// has, so the page reads this preference again on every visit to take up a change made in Settings,
/// and a choice shown here that the file refused would be undone, silently, by the next trip to
/// Settings and back. Here the box goes back at once, and <see cref="Save"/> says why.</para>
/// </summary>
public sealed partial class AlreadyClearSwitch : ObservableObject
{
    private readonly PreferenceService _preferences;

    public AlreadyClearSwitch(PreferenceService preferences) => _preferences = preferences;

    public bool IsOn
    {
        get => _preferences.Current.ShowAlreadyClear;
        set
        {
            Save.Failed = _preferences.Update(current => current with { ShowAlreadyClear = value }) is SaveOutcome.Refused;

            // Whatever the outcome. After a refused write the box shows a value that is not in
            // effect until it reads this again.
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the last change could not be written, which the page states beside the box.</summary>
    public SettingSave Save { get; } = new();

    /// <summary>Read the preference again, after a change made somewhere else, such as on the Settings page.</summary>
    public void Follow() => OnPropertyChanged(nameof(IsOn));
}
