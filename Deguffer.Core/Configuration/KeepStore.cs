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
/// <para>Every failure to read degrades to keeping nothing, for the same reason: that is the narrow
/// failure rather than the dangerous one. A kept item offered again still has the preview in front of
/// it, and above Tier 1 it arrives unticked and behind §7's confirmation. It is not free: a Tier 1
/// item offered again starts ticked. No Tier 1 provider names its items today.</para>
/// </summary>
public sealed class KeepStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _file;

    public KeepStore(IUserEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        _file = Path.Combine(_directory, "keep.json");
    }

    /// <summary>The kept items, or none where the file is missing, unreadable or corrupt.</summary>
    public KeepList Load()
    {
        try
        {
            var json = File.ReadAllText(LongPath.Extended(_file));

            return Usable(JsonSerializer.Deserialize<KeptItem?[]>(json, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing on first run, unreadable, or hand-edited into something that is not a list.
            return KeepList.Empty;
        }
    }

    /// <summary>
    /// Persist <paramref name="list"/>. Returns whether it was written, so a caller can say that a
    /// choice will not survive a restart rather than implying it will.
    /// </summary>
    public bool Save(KeepList list)
    {
        ArgumentNullException.ThrowIfNull(list);

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
    /// What was read, in the shape <see cref="KeptItem"/> declares.
    ///
    /// <para>A null entry, an entry with no item, and an item with no key are all well-formed JSON, so
    /// none of them reaches the catch above. Each costs its own line and nothing else. A missing name
    /// falls back to the key rather than dropping the entry, because the key is what protects the item
    /// and the name only describes it.</para>
    /// </summary>
    private static KeepList Usable(KeptItem?[]? stored)
    {
        if (stored is null)
        {
            // The literal `null` document.
            return KeepList.Empty;
        }

        var usable = new List<KeptItem>(stored.Length);

        foreach (var item in stored)
        {
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
