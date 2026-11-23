using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Deguffer.Core.Health.Parsing;

namespace Deguffer.Core.Health;

/// <summary>
/// The models one manufacturer's document names, and what it says each attribute means on them. A maker
/// with several documents keys each on its own models, because their layouts differ even where the IDs
/// match.
/// </summary>
internal sealed record AtaAttributeDocument(Regex Models, FrozenDictionary<byte, AtaAttributeDefinition> Attributes)
{
    /// <summary>
    /// The meaning that the first of <paramref name="documents"/> naming <paramref name="drive"/> gives
    /// <paramref name="id"/>, or null where none names the drive or its document leaves the attribute out.
    /// </summary>
    internal static AtaAttributeDefinition? Of(ReadOnlySpan<AtaAttributeDocument> documents, byte id, DeviceDescriptor drive)
    {
        foreach (var document in documents)
        {
            if (document.Models.IsMatch(drive.Model))
            {
                return document.Attributes.GetValueOrDefault(id);
            }
        }

        return null;
    }
}
