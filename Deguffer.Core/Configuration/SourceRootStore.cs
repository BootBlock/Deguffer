using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// The two property names in the file, which are a stored format rather than an implementation
    /// detail: renaming <see cref="SourceRoot.RemoteStorageApproved"/> must not silently drop every
    /// approval the user has given. <see cref="StoredRoot"/> holds them to the names, and the read
    /// below uses the same constants, so the written shape and the read shape cannot drift.
    /// </summary>
    private const string PathProperty = "path";

    private const string ApprovedProperty = "remoteStorageApproved";

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
    public IReadOnlyList<SourceRoot> Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));
            var stored = JsonSerializer.Deserialize<JsonElement[]>(json, SerializerOptions);

            if (stored is null)
            {
                return [];
            }

            return Usable(stored.Select(Entry).OfType<SourceRoot>());
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
    public bool Save(IReadOnlyList<SourceRoot> roots, out IReadOnlyList<SourceRoot> stored)
    {
        ArgumentNullException.ThrowIfNull(roots);

        stored = Usable(roots);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(
                LongPath.Extended(_file),
                JsonSerializer.Serialize(
                    stored.Select(root => new StoredRoot(root.Path, root.RemoteStorageApproved)),
                    SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stored = [];
            return false;
        }
    }

    /// <summary>Persist <paramref name="roots"/> where the caller keeps no copy of its own.</summary>
    public bool Save(IReadOnlyList<SourceRoot> roots) => Save(roots, out _);

    /// <summary>
    /// One stored entry, in either form the file may hold it in, or null where it is not usable.
    ///
    /// <para><b>A bare string is read as well as an object, rather than the file being migrated on
    /// load.</b> Every folder approved before Deguffer asked about cloud mounts is stored as a string,
    /// and a read that rewrote the file to bring it forward would lose the user's approvals outright
    /// on the profile where that write is what fails. A string carries no approval, which is the
    /// narrow reading of an answer nobody was asked for — see
    /// <see cref="SourceRoot.RemoteStorageApproved"/>. The next <see cref="Save"/> writes the new
    /// shape for every entry, so the old one disappears the first time the user changes anything.</para>
    ///
    /// <para>Each property is read through its own kind check rather than by deserialising the
    /// element, so a hand-edited entry with a number where the path should be costs that entry and
    /// not the file.</para>
    /// </summary>
    private static SourceRoot? Entry(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new SourceRoot(element.GetString()!),
        JsonValueKind.Object => Text(element, PathProperty) is { } path
            ? new SourceRoot(path, Flag(element, ApprovedProperty))
            : null,
        _ => null,
    };

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The roots the rest of the code may rely on: absolute, resolved, and each folder once.
    ///
    /// A root has to be an absolute path: a relative one would resolve against whatever directory
    /// the process happens to be running in, which is not something the user consented to. It also
    /// has to be <em>resolved</em>, and that second half is what <see cref="LongPath.Configured"/>
    /// is for. Every other configured path in Deguffer already goes through it; this one did not,
    /// and once several providers share one discovery pass the omission stops being cosmetic. The
    /// walk resolves a root itself, by way of <see cref="LongPath.Extended"/>, while the volume
    /// index narrows by comparing strings — so a hand-edited <c>C:/Users/me/src</c> or a value
    /// carrying <c>..</c> would make the two routes disagree, and an elevated run would quietly
    /// return an empty plan where an unelevated one found everything.
    ///
    /// <para>The first entry for a folder wins where the file names it twice. That is the same
    /// folder said twice rather than two decisions, and it cannot be read as one: an approval and a
    /// refusal of the same path are indistinguishable in the file from an approval written twice.
    /// Taking the first keeps the rule "what reached disk is what is read back" true, because the
    /// same narrowing runs on the way in.</para>
    /// </summary>
    private static IReadOnlyList<SourceRoot> Usable(IEnumerable<SourceRoot> roots) =>
    [
        .. roots
            .Select(root => LongPath.Configured(root.Path) is { } path
                ? root with { Path = path }
                : null)
            .OfType<SourceRoot>()
            .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// One entry as it is written, holding the property names in <see cref="PathProperty"/> and
    /// <see cref="ApprovedProperty"/> rather than taking whatever <see cref="SourceRoot"/>'s members
    /// happen to be called.
    /// </summary>
    private sealed record StoredRoot(
        [property: JsonPropertyName(PathProperty)] string Path,
        [property: JsonPropertyName(ApprovedProperty)] bool RemoteStorageApproved);
}
