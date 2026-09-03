using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Reads and writes what each Storage row was left ticked as, as JSON under
/// <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// A third file rather than a field on <see cref="AppPreferences"/>, for the reason
/// <see cref="SourceRootStore"/> gives: that record documents itself as presentation-only, and this
/// is not. It decides what arrives ticked, which decides what a user who does not re-read the list
/// removes. Keeping it apart keeps that invariant true rather than leaving a comment that has
/// quietly stopped being so.
///
/// Every failure degrades to remembering nothing, which is the shipped behaviour: every row starts
/// at its §3 tier default. That is not the narrowest imaginable answer — a user who unticked a
/// Tier 1 row finds it ticked again — but it is the honest one, because a memory that cannot be
/// read is not a decision the user made. The preview and the confirmations still stand in front of
/// it, and <see cref="SelectionMemory"/> keeps Tier 3 unticked whatever this file says.
/// </summary>
public sealed class SelectionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Nothing remembered — every failure below lands here.</summary>
    private static readonly Dictionary<string, RememberedSelection> Empty = [];

    private readonly string _directory;
    private readonly string _file;

    public SelectionStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "selection.json");
    }

    /// <summary>The remembered rows, or none where the file is missing, unreadable or corrupt.</summary>
    public IReadOnlyDictionary<string, RememberedSelection> Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));

            return JsonSerializer.Deserialize<Dictionary<string, RememberedSelection>>(json, SerializerOptions)
                ?? Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or hand-edited into nonsense.
            return Empty;
        }
    }

    /// <summary>
    /// Persist <paramref name="selections"/>. Returns whether it was written, so a caller that
    /// cares can avoid implying a choice will survive a restart when it will not.
    /// </summary>
    public bool Save(IReadOnlyDictionary<string, RememberedSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(selections, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
