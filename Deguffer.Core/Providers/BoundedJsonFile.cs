using System.Text.Json;

namespace Deguffer.Core.Providers;

/// <summary>
/// A small JSON record another program wrote, read no further than a stated size. The bound, and
/// the reason nothing read here is ever shown, are <see cref="BoundedFile"/>'s.
/// </summary>
internal static class BoundedJsonFile
{
    /// <summary>
    /// The record, or null where the file is missing, larger than <paramref name="maximumBytes"/>,
    /// unreadable, not JSON, or not a JSON object. The caller disposes what it is given.
    /// </summary>
    public static JsonDocument? Read(string path, int maximumBytes)
    {
        return BoundedFile.Read(path, maximumBytes) is { } content ? Parse(content) : null;
    }

    /// <summary>
    /// A record already read within its bound, or null where it is not JSON or not a JSON object. The
    /// caller disposes what it is given.
    /// </summary>
    public static JsonDocument? Parse(ReadOnlyMemory<byte> content)
    {
        try
        {
            var document = JsonDocument.Parse(content);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            // Caught halfway through being written. That is not the record either, and the caller
            // reads null as "could not tell".
            return null;
        }
    }

    /// <summary>A string property of the record, or null where it is absent or is not a string.</summary>
    public static string? StringProperty(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
