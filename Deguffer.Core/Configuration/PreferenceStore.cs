using System.Diagnostics.CodeAnalysis;
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
///
/// <para><b>The one store still read by reflection.</b> Every other store reads through a contract
/// generated at build time (see <see cref="IndentedStoreJson"/>). The generator in .NET 10 builds a
/// type with <c>init</c> members in one object initialiser, so a member the file leaves out is set to
/// null or zero rather than keeping its initialiser: a file from before the scan settings existed would
/// read with no scan settings at all. Most of <see cref="AppPreferences"/>' groups are such members.
/// This file moves to a generated contract once the runtime sets only the members a file names
/// (dotnet/runtime#124650), and <c>PreferenceStoreTests</c> are what tell the two apart.</para>
/// </summary>
public sealed class PreferenceStore
{
    private const string ReflectionJustification =
        "Deguffer is never trimmed, and the generated contract loses the default of every init member a file omits.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },

        // A hand-edited null in a group of settings, such as "Scanning", would otherwise arrive as a
        // null the type says cannot be there. Refused here, it is a corrupt file like any other.
        RespectNullableAnnotations = true,
    };

    private readonly string _file;

    public PreferenceStore(IUserEnvironment environment)
    {
        _file = Path.Combine(environment.LocalAppData, "Deguffer", "preferences.json");
    }

    /// <summary>
    /// The stored preferences, or <see cref="AppPreferences.Default"/> if none can be read.
    ///
    /// Every failure here degrades to the defaults deliberately. A settings file is not worth
    /// failing a launch over, and a tool whose premise is trustworthiness around deletion must not
    /// be blocked from starting by a stray byte in a cosmetic preference.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = ReflectionJustification)]
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
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = ReflectionJustification)]
    public bool Save(AppPreferences preferences)
    {
        try
        {
            StoreFile.Replace(_file, JsonSerializer.Serialize(preferences, SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
