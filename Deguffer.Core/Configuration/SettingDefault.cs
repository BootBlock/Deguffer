namespace Deguffer.Core.Configuration;

/// <summary>
/// One setting the Settings page shows, held against the value Deguffer ships with: whether a set of
/// preferences is on that value, how to put it back, and how to say both in words.
///
/// <para>Several of these settings widen or narrow what gets deleted, so a reader who changed one has
/// to be able to tell later whether they are still on it, and get back to it without losing anything
/// else. The shipped value is read from <see cref="AppPreferences.Default"/> rather than written out
/// here, so the row's statement and the reset cannot disagree with what a fresh install holds.</para>
/// </summary>
public sealed class SettingDefault
{
    private readonly Func<AppPreferences, bool> _isShipped;
    private readonly Func<AppPreferences, AppPreferences> _reset;
    private readonly Func<AppPreferences, string> _says;

    private SettingDefault(
        string title,
        string name,
        string shipped,
        Func<AppPreferences, bool> isShipped,
        Func<AppPreferences, AppPreferences> reset,
        Func<AppPreferences, string> says)
    {
        Title = title;
        Name = name;
        Shipped = shipped;
        Statement = $"Default: {shipped}";
        _isShipped = isShipped;
        _reset = reset;
        _says = says;
    }

    /// <summary>The title of the row that sets it, as the page shows it.</summary>
    public string Title { get; }

    /// <summary>
    /// What a list of several settings calls it: the title, and the kind of drive where the row sets one
    /// kind's value, as in <c>Listing buffer (NVMe SSD)</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>The shipped value in words, as the control shows a value: <c>7 days</c>, <c>On</c>, <c>Auto</c>.</summary>
    public string Shipped { get; }

    /// <summary>The line the row shows under its description: <c>Default: 7 days</c>.</summary>
    public string Statement { get; }

    /// <summary>Whether <paramref name="preferences"/> holds the shipped value of this setting.</summary>
    public bool IsDefault(AppPreferences preferences) => _isShipped(preferences);

    /// <summary><paramref name="preferences"/> with this setting on its shipped value, and every other as it was.</summary>
    public AppPreferences Reset(AppPreferences preferences) => _reset(preferences);

    /// <summary>What <paramref name="preferences"/> hold for this setting, in the words <see cref="Shipped"/> uses.</summary>
    public string Says(AppPreferences preferences) => _says(preferences);

    /// <param name="title">The row's title.</param>
    /// <param name="kind">The kind of drive the row sets a value for, or null for a setting of the whole machine.</param>
    /// <param name="read">The setting's value in a set of preferences.</param>
    /// <param name="write">A set of preferences with the setting at a value.</param>
    /// <param name="say">A value in words.</param>
    internal static SettingDefault Of<T>(
        string title,
        string? kind,
        Func<AppPreferences, T> read,
        Func<AppPreferences, T, AppPreferences> write,
        Func<T, string> say)
    {
        var shipped = read(AppPreferences.Default);

        return new SettingDefault(
            title,
            kind is null ? title : $"{title} ({kind})",
            say(shipped),
            preferences => EqualityComparer<T>.Default.Equals(read(preferences), shipped),
            preferences => write(preferences, shipped),
            preferences => say(read(preferences)));
    }
}
