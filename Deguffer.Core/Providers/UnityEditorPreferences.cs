using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Unity Editor's own preferences, which Unity calls <c>EditorPrefs</c>, as every Editor since
/// Unity 5 keeps them on Windows: values under one key in the current user's hive, shared by every
/// installed version.
///
/// <para><b>A value is named for its preference and a hash.</b> Unity appends <c>_h</c> and a hash
/// of the preference's name in decimal, so <c>GICacheFolder</c> is stored under a name such as
/// <c>GICacheFolder_h</c> followed by digits. The names are listed and matched on that shape rather
/// than the hash computed, so a value is found by what Unity wrote rather than by a formula nobody
/// here has watched Unity apply.</para>
///
/// <para><b>A text preference is bytes.</b> Unity stores <c>EditorPrefs.SetString</c> as a
/// <c>REG_BINARY</c> of UTF-8 ending in a NUL, and <c>SetBool</c> and <c>SetInt</c> as a
/// <c>REG_DWORD</c>. A <c>REG_SZ</c> is read as well, because no Unity install has been examined
/// to rule one out, and a preference read as absent is a moved cache the plan never mentions.</para>
/// </summary>
internal sealed class UnityEditorPreferences
{
    /// <summary>The key under <c>HKEY_CURRENT_USER</c>, named for Unity 5 and kept by every later version.</summary>
    public const string Key = @"Software\Unity Technologies\Unity Editor 5.x";

    private const string HashSeparator = "_h";

    private readonly IUserEnvironment _environment;
    private readonly IReadOnlyList<string> _valueNames;

    private UnityEditorPreferences(IUserEnvironment environment, IReadOnlyList<string> valueNames)
    {
        _environment = environment;
        _valueNames = valueNames;
    }

    /// <summary>The preferences recorded on <paramref name="environment"/>'s machine, listed once.</summary>
    public static UnityEditorPreferences Read(IUserEnvironment environment) =>
        new(environment, environment.ReadCurrentUserRegistryValueNames(Key));

    /// <summary>
    /// Whether the <c>EditorPrefs.SetBool</c> preference <paramref name="preference"/> is on. False
    /// where it was never set, which is Unity's own default for a flag.
    /// </summary>
    public bool IsOn(string preference) =>
        ValueName(preference) is { } name && _environment.ReadCurrentUserRegistryNumber(Key, name) is { } value && value != 0;

    /// <summary>
    /// The <c>EditorPrefs.SetString</c> preference <paramref name="preference"/>, or null where it
    /// was never set or holds nothing.
    /// </summary>
    public string? Text(string preference)
    {
        if (ValueName(preference) is not { } name)
        {
            return null;
        }

        var text = _environment.ReadCurrentUserRegistryBytes(Key, name) is { } bytes
            ? Encoding.UTF8.GetString(bytes).TrimEnd('\0')
            : _environment.ReadCurrentUserRegistryValue(Key, name);

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The value Unity stores <paramref name="preference"/> under: its name, <c>_h</c>, and nothing
    /// but digits. Compared ordinally, as Unity's preference names are, so a preference that differs
    /// only in case is a different preference.
    /// </summary>
    private string? ValueName(string preference) =>
        _valueNames.FirstOrDefault(name =>
            name.Length > preference.Length + HashSeparator.Length
            && name.StartsWith(preference + HashSeparator, StringComparison.Ordinal)
            && name.AsSpan(preference.Length + HashSeparator.Length).IndexOfAnyExceptInRange('0', '9') < 0);
}
