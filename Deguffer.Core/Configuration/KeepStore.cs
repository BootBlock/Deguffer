using System.Text.Json;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Configuration;

/// <summary>
/// Reads and writes the keep list, as JSON under <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// <para><b>A file of its own, and never <c>selection.json</c>.</b> A remembered selection and a
/// remembered protection have opposite safety polarity. One file holding both is how a bug comes to
/// read one as the other, and the direction that bug fails in is a restored tick on a Tier 3 row,
/// which is the one thing <see cref="SelectionMemory"/> exists to refuse. Kept apart, the worst a
/// misread here can do is offer an item the user wanted kept.</para>
///
/// <para>A failure to read degrades to keeping nothing for the session, for the same reason: that is
/// the narrow failure rather than the dangerous one. A kept item offered again still has the preview
/// in front of it, and above Tier 1 it arrives unticked and behind §7's confirmation. It is not free:
/// a Tier 1 item offered again starts ticked. No Tier 1 provider names its items today.</para>
///
/// <para><b>A file it could not read is a file it will not write over.</b> Keeping nothing is safe
/// for a session and ruinous for the file. The next item kept would be saved on top of a list that was
/// only unreadable — held open by a scanner, or torn by an interrupted save — and every entry in it
/// would be gone for good, which turns a session's lapse into the permanent loss this list exists to
/// prevent. So a read that fails for any reason other than there being no file leaves this store
/// refusing to save, and the caller says so.</para>
/// </summary>
public sealed class KeepStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _file;

    private bool _refusesToSave;

    public KeepStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "keep.json");
    }

    /// <summary>The kept items, or none where the file is missing, unreadable or corrupt.</summary>
    public KeepList Load()
    {
        _refusesToSave = false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(LongPath.Extended(_file)));

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                // Well-formed and not a list, so what it holds is unknown. It is not written over.
                _refusesToSave = true;
                return KeepList.Empty;
            }

            return Usable(document.RootElement);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The first run, which has nothing to lose by writing a file.
            return KeepList.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Held open, refused, or torn. Its entries may all still be there.
            _refusesToSave = true;
            return KeepList.Empty;
        }
    }

    /// <summary>
    /// Whether the last <see cref="Load"/> found a file it could not read, so nothing will be written
    /// over it. A caller says so in those words rather than as a failed write: the folder is writable,
    /// and the file is what needs attention.
    /// </summary>
    public bool RefusesToSave => _refusesToSave;

    /// <summary>
    /// Persist <paramref name="list"/>. Returns whether it was written, so a caller can say that a
    /// choice will not survive a restart rather than implying it will. False as well, and nothing
    /// written, where <see cref="RefusesToSave"/>.
    /// </summary>
    public bool Save(KeepList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (_refusesToSave)
        {
            return false;
        }

        var written = _file + ".tmp";

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));

            // Written beside the file and moved over it, so a save that is interrupted leaves the
            // previous list whole rather than a truncated one the next launch cannot read.
            File.WriteAllText(LongPath.Extended(written), JsonSerializer.Serialize(list.Items, SerializerOptions));
            File.Move(LongPath.Extended(written), LongPath.Extended(_file), overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Each entry of the list, read field by field in the shape <see cref="KeptItem"/> declares.
    ///
    /// <para>Field by field rather than deserialised whole, so damage costs only what it touches. An
    /// entry is dropped only where its provider or its key cannot be read, because those are the two
    /// things that match an item, and an entry missing either protected nothing. A name that is missing
    /// or of the wrong type falls back to the provider's id or the item's key: it only describes the
    /// item, and dropping the entry for it would offer the item again and remove it from the file at
    /// the next save.</para>
    /// </summary>
    private static KeepList Usable(JsonElement entries)
    {
        var usable = new List<KeptItem>(entries.GetArrayLength());

        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, nameof(KeptItem.ProviderId)) is not { } providerId
                || !entry.TryGetProperty(nameof(KeptItem.Item), out var item)
                || Text(item, nameof(ItemIdentity.Key)) is not { } key)
            {
                continue;
            }

            usable.Add(new KeptItem(
                providerId,
                Text(entry, nameof(KeptItem.ProviderName)) ?? providerId,
                new ItemIdentity(key, Text(item, nameof(ItemIdentity.Name)) ?? key)));
        }

        return new KeepList(usable);
    }

    /// <summary>A named field's text, or null where the element is not an object or the field is not usable text.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
