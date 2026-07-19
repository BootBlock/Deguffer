using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// The folders the user has approved Deguffer to look for source-tree build output in, stored as
/// JSON under <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// Deliberately a separate file from <see cref="AppPreferences"/> rather than a field on it. That
/// record documents itself as presentation-only — switching the backdrop off changes nothing about
/// what gets deleted — and approved roots are the first setting that does change it. Keeping the
/// two apart keeps that invariant true instead of leaving a comment that has quietly stopped being
/// so, and it matches the difference in stakes between the two files.
///
/// <see cref="PreferenceStore"/>'s degrade-to-default behaviour is the model, with one asymmetry
/// that matters: the safe default here is <em>empty</em>. A corrupt or unreadable roots file must
/// never widen what Deguffer will consider, so every failure narrows scope to nothing rather than
/// falling back to anything plausible.
/// </summary>
public sealed class SourceRootStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _file;

    public SourceRootStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "source-roots.json");
    }

    /// <summary>
    /// The approved roots, or an empty list if none can be read.
    ///
    /// Entries that are not rooted absolute paths are dropped rather than failing the whole read:
    /// a hand-edited file with one bad line should cost the user that line, not every root they
    /// approved. Nothing here checks the directories exist — a root on a disconnected drive is
    /// still approved, and discovery treats it as finding nothing.
    /// </summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));
            var stored = JsonSerializer.Deserialize<string[]>(json, SerializerOptions);

            if (stored is null)
            {
                return [];
            }

            return [.. stored.Where(IsUsableRoot).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or corrupt. All three mean no root is approved,
            // which is the narrow answer rather than the convenient one.
            return [];
        }
    }

    /// <summary>
    /// Persist <paramref name="roots"/>. Returns whether it was written, so a caller can tell the
    /// user their choice will not survive a restart rather than implying it was saved.
    /// </summary>
    /// <param name="stored">
    /// What actually reached disk, which is not always what was asked for: unusable entries are
    /// dropped. A caller holding its own copy must adopt this rather than its own list, or the
    /// approved roots it shows and the ones Deguffer will search stop being the same set.
    /// </param>
    public bool Save(IReadOnlyList<string> roots, out IReadOnlyList<string> stored)
    {
        ArgumentNullException.ThrowIfNull(roots);

        stored = [.. roots.Where(IsUsableRoot).Distinct(StringComparer.OrdinalIgnoreCase)];

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(stored, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stored = [];
            return false;
        }
    }

    /// <summary>Persist <paramref name="roots"/> where the caller keeps no copy of its own.</summary>
    public bool Save(IReadOnlyList<string> roots) => Save(roots, out _);

    /// <summary>
    /// A root has to be an absolute path. A relative one would resolve against whatever directory
    /// the process happens to be running in, which is not something the user consented to.
    /// </summary>
    private static bool IsUsableRoot(string? root) =>
        !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root);
}
