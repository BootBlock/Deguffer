using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Settings page's way back to the shipped value of every setting it shows, the scanning values
/// included. Which settings those are, and the question asked first, are Core's
/// <see cref="ShippedSettings"/> and <see cref="RestoreDefaultsPrompt"/>.
///
/// <para>It writes the preferences and nothing else. The keep list, the source folders and the
/// emulator folders are lists the reader built, kept by services of their own, which this never
/// reaches.</para>
/// </summary>
public sealed class SettingsRestore : ObservableObject
{
    private readonly PreferenceService _preferences;
    private readonly LatestSave _latest;

    /// <param name="latest">The page's one notice of a failed save, shared with every form on the page.</param>
    public SettingsRestore(PreferenceService preferences, LatestSave latest)
    {
        _preferences = preferences;
        _latest = latest;
        CanRestore = ShippedSettings.AnyChanged(preferences.Current);
    }

    /// <summary>
    /// Whether any setting on the page is off its default, which is when the page offers to restore
    /// them. Held rather than read each time, so the button is told only when the answer turns over.
    /// </summary>
    public bool CanRestore { get; private set; }

    /// <summary>Whether the last restore could not be written, which the page says at its foot, where it was asked for.</summary>
    public SettingSave Save { get; } = new();

    /// <summary>What the reader is asked first, or null where every setting is already on its default.</summary>
    public RestoreDefaultsPrompt? Prompt() => RestoreDefaultsPrompt.For(_preferences.Current);

    /// <summary>
    /// Put every setting on the page back to its default, in one write. The forms then read back what
    /// was stored, as the page tells them to, whether or not it was written.
    /// </summary>
    public void Restore()
    {
        _latest.Report(Save, _preferences.Update(ShippedSettings.RestoreAll));
        Follow();
    }

    /// <summary>Read the restore's answer again, after any write on the page, and tell the button only if it turned over.</summary>
    public void Follow()
    {
        var canRestore = ShippedSettings.AnyChanged(_preferences.Current);

        if (canRestore != CanRestore)
        {
            CanRestore = canRestore;
            OnPropertyChanged(nameof(CanRestore));
        }
    }
}
