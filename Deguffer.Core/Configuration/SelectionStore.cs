using System.Collections.ObjectModel;
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

    /// <summary>
    /// Nothing remembered — every failure below lands here, and every step-less entry gets the
    /// second one. Both are shared, so they are the immutable empties rather than a
    /// <see cref="Dictionary{TKey, TValue}"/> handed out behind a read-only interface that anything
    /// could cast away and write into for the life of the process.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RememberedSelection> Empty =
        ReadOnlyDictionary<string, RememberedSelection>.Empty;

    private static readonly IReadOnlyDictionary<string, bool> NoSteps =
        ReadOnlyDictionary<string, bool>.Empty;

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

            return Usable(
                JsonSerializer.Deserialize<Dictionary<string, RememberedSelection>>(json, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or hand-edited into nonsense.
            return Empty;
        }
    }

    /// <summary>
    /// What was read, in the shape <see cref="RememberedSelection"/> declares.
    ///
    /// <c>{"npm": null}</c> and <c>{"npm": {"IsSelected": false}}</c> are both well-formed JSON, so
    /// neither reaches the catch above; they arrive as a null entry and a null step map, against
    /// two members that say they are never null. Read straight out, the first stops the app opening
    /// — the memory is built from a static initialiser — and the second throws at the first row the
    /// preview draws. One bad line costs the provider it names, and nothing else.
    ///
    /// Answered here rather than by a guard further down, so the declared types are true of every
    /// value anything downstream is handed.
    /// </summary>
    private static IReadOnlyDictionary<string, RememberedSelection> Usable(
        Dictionary<string, RememberedSelection>? stored)
    {
        if (stored is null)
        {
            // The literal `null` document.
            return Empty;
        }

        Dictionary<string, RememberedSelection> usable = new(StringComparer.Ordinal);

        foreach (var (providerId, selection) in stored)
        {
            if (selection is not null)
            {
                usable[providerId] = selection with { Steps = selection.Steps ?? NoSteps };
            }
        }

        return usable;
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
