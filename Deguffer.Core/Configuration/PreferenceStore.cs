using System.Text.Json;
using System.Text.Json.Serialization;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Reads and writes <see cref="AppPreferences"/> as JSON under <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// This lives in Core rather than the app so it can be tested against a fake environment: the
/// interesting behaviour is what happens to a file that is missing, truncated or hand-edited into
/// nonsense, and none of that is reachable through the WinUI shell.
/// </summary>
public sealed class PreferenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly string _file;

    public PreferenceStore(IUserEnvironment environment)
    {
        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "preferences.json");
    }

    /// <summary>
    /// The stored preferences, or <see cref="AppPreferences.Default"/> if none can be read.
    ///
    /// Every failure here degrades to the defaults deliberately. A settings file is not worth
    /// failing a launch over, and a tool whose premise is trustworthiness around deletion must not
    /// be blocked from starting by a stray byte in a cosmetic preference.
    /// </summary>
    public AppPreferences Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));
            return JsonSerializer.Deserialize<AppPreferences>(json, SerializerOptions) ?? AppPreferences.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run (IOException/FileNotFoundException), unreadable, or corrupt.
            return AppPreferences.Default;
        }
    }

    /// <summary>
    /// Persist <paramref name="preferences"/>. Returns whether it was written — a caller that
    /// cares can tell the user their choice will not survive a restart, rather than silently
    /// implying it was saved.
    /// </summary>
    public bool Save(AppPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(preferences, SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
