using System.Text.Json;
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
    /// Persist <paramref name="list"/>. Returns whether it was written, so a caller can say that a
    /// choice will not survive a restart rather than implying it will. False as well, and nothing
    /// written, where the last <see cref="Load"/> found a file it could not read.
    /// </summary>
    public bool Save(KeepList list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (_refusesToSave)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(LongPath.Extended(_directory));
            File.WriteAllText(LongPath.Extended(_file), JsonSerializer.Serialize(list.Items, SerializerOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Each entry of the list, in the shape <see cref="KeptItem"/> declares.
    ///
    /// <para>Read one entry at a time, so a damaged entry costs its own line and nothing else: a null,
    /// an entry with no item, an item with no key, and a field of the wrong type. A whole-list read
    /// would throw on the last of those and lose every entry beside it. A missing name falls back to
    /// the key rather than dropping the entry, because the key is what protects the item and the name
    /// only describes it.</para>
    /// </summary>
    private static KeepList Usable(JsonElement entries)
    {
        var usable = new List<KeptItem>(entries.GetArrayLength());

        foreach (var entry in entries.EnumerateArray())
        {
            KeptItem? item;

            try
            {
                item = entry.Deserialize<KeptItem>(SerializerOptions);
            }
            catch (JsonException)
            {
                // A field of the wrong type, which matches nothing and so protected nothing.
                continue;
            }

            if (item is not { Item.Key: { } key }
                || string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(item.ProviderId))
            {
                continue;
            }

            usable.Add(item with
            {
                ProviderName = string.IsNullOrWhiteSpace(item.ProviderName) ? item.ProviderId : item.ProviderName,
                Item = item.Item with { Name = string.IsNullOrWhiteSpace(item.Item.Name) ? key : item.Item.Name },
            });
        }

        return new KeepList(usable);
    }
}
