using Deguffer.Core.Configuration;

namespace Deguffer.App.Shell;

/// <summary>
/// A preference that governs what a page draws rather than what gets deleted, such as a picture, a
/// colouring or a panel the reader put away. It is shown first and written second.
///
/// <para>That inverts <see cref="PreferenceService.Update"/>'s own order. That order exists so a
/// rejected write cannot take effect for a setting governing what gets deleted. A reader who closes
/// something over their picture should not find it still there because a file could not be written,
/// so a choice of this kind takes effect for the session whether or not it reaches the file.</para>
/// </summary>
internal sealed class DrawnSetting<T>
{
    private readonly PreferenceService _preferences;
    private readonly Func<AppPreferences, T> _read;
    private readonly Func<AppPreferences, T, AppPreferences> _write;
    private readonly Action<T> _show;

    /// <param name="preferences">Where the setting is kept.</param>
    /// <param name="read">The setting's value in the preferences.</param>
    /// <param name="write">The preferences with the setting at a new value.</param>
    /// <param name="show">Puts a value on screen.</param>
    public DrawnSetting(
        PreferenceService preferences,
        Func<AppPreferences, T> read,
        Func<AppPreferences, T, AppPreferences> write,
        Action<T> show)
    {
        _preferences = preferences;
        _read = read;
        _write = write;
        _show = show;
    }

    /// <summary>
    /// The value the preferences hold. A page reads it once, as it is built: read again on a later
    /// visit, it would undo a choice whose write failed, silently and in the same session.
    /// </summary>
    public T Stored => _read(_preferences.Current);

    /// <summary>Show <paramref name="value"/>, then write it.</summary>
    /// <returns>Whether the value reached the preferences file.</returns>
    public bool Choose(T value)
    {
        _show(value);

        return _preferences.Update(current => _write(current, value));
    }
}
