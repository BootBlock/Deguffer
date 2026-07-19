using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// The live settings, and the one place that writes them back. The window listens so a change on
/// the Settings page reaches the shell immediately rather than at next launch.
///
/// This holds the values and persists them; it does not apply them. Turning
/// <see cref="AppPreferences.Theme"/> into an <c>ElementTheme</c> is the window's job, and turning
/// it into a combo-box index is the view-model's.
/// </summary>
public sealed class PreferenceService
{
    private readonly PreferenceStore _store;

    public PreferenceService(PreferenceStore store)
    {
        _store = store;
        Current = store.Load();
    }

    public AppPreferences Current { get; private set; }

    public event EventHandler? Changed;

    /// <summary>
    /// Persist <paramref name="change"/> and, if that succeeded, apply it. Returns whether it
    /// reached disk — a caller that says nothing on false is claiming a save that did not happen.
    ///
    /// The write comes first so a failure leaves memory and disk agreeing. Applying first meant a
    /// rejected write still took effect for the session: switching the confirmation off on a
    /// read-only profile stopped the prompts appearing while reporting that it had not saved, so
    /// the user was told their choice was discarded and then ran without the prompt anyway.
    /// </summary>
    public bool Update(Func<AppPreferences, AppPreferences> change)
    {
        var updated = change(Current);
        if (updated == Current)
        {
            return true;
        }

        if (!_store.Save(updated))
        {
            return false;
        }

        Current = updated;
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }
}
