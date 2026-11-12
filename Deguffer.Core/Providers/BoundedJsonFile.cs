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
    /// <remarks>An array at the top level is <see cref="ReadArray"/>'s.</remarks>
    /// <param name="options">
    /// What the writer allows beyond JSON itself, such as the comments Visual Studio writes into its
    /// settings file. The default is strict JSON.
    /// </param>
    public static JsonDocument? Read(string path, int maximumBytes, JsonDocumentOptions options = default)
    {
        return BoundedFile.Read(path, maximumBytes) is { } content ? Parse(content, options) : null;
    }

    /// <summary>
    /// A record already read within its bound, or null where it is not JSON or not a JSON object. The
    /// caller disposes what it is given.
    /// </summary>
    public static JsonDocument? Parse(ReadOnlyMemory<byte> content, JsonDocumentOptions options = default) =>
        Parse(content, JsonValueKind.Object, options);

    /// <summary>
    /// A record whose top level is a JSON array, such as an editor's list of installed extensions, or
    /// null where the file is missing, larger than <paramref name="maximumBytes"/>, unreadable, not
    /// JSON, or not an array. The caller disposes what it is given.
    /// </summary>
    public static JsonDocument? ReadArray(string path, int maximumBytes) =>
        BoundedFile.Read(path, maximumBytes) is { } content ? Parse(content, JsonValueKind.Array, default) : null;

    private static JsonDocument? Parse(ReadOnlyMemory<byte> content, JsonValueKind root, JsonDocumentOptions options)
    {
        try
        {
            var document = JsonDocument.Parse(content, options);

            if (document.RootElement.ValueKind == root)
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
