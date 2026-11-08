using System.Buffers;
using System.Text.Json;

namespace Deguffer.Core.Safety;

/// <summary>The two fields of a restore manifest that say which project it was written for.</summary>
/// <param name="SchemaVersion">The root's <c>version</c>, or null if it is absent or not a 32-bit integer.</param>
/// <param name="ProjectPath">
/// <c>project.restore.projectPath</c>, or null if it is absent, not a string, or not valid UTF-8.
/// </param>
internal sealed record RestoreManifestIdentity(int? SchemaVersion, string? ProjectPath);

/// <summary>
/// Reads the identity of a <c>project.assets.json</c> without building a document of it.
///
/// A real manifest runs to about a megabyte, almost all of it <c>targets</c> and <c>libraries</c>,
/// and two values are wanted. A <see cref="JsonDocument"/> buffers the whole file and records every
/// token to answer that, so this walks the tokens once through a small window and keeps only what
/// it was asked for.
///
/// It still reads to the end of the document. NuGet writes <c>project</c> after everything else, so
/// stopping once both values are in hand would save almost nothing, and it would accept a manifest
/// cut off after them. Validating the whole document is what makes a truncated file a
/// <see cref="JsonException"/>, and so Tier 4.
/// </summary>
internal static class RestoreManifestReader
{
    /// <summary>
    /// The window the tokens are read through. It grows only when one token is larger than it, which
    /// a manifest's longest strings, its file paths, never are.
    /// </summary>
    private const int WindowBytes = 16 * 1024;

    /// <summary>JsonDocument skipped a leading UTF-8 byte order mark, and the reader does not.</summary>
    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// The identity the manifest declares, or null if it declares a field of it more than once.
    ///
    /// A duplicate is refused rather than resolved. <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// took the last definition, but a manifest that names its project twice is hand-edited or
    /// corrupt, and which name it means cannot be known. Duplicates anywhere outside the identity
    /// are accepted, as they always were.
    /// </summary>
    /// <exception cref="JsonException">The document is not one complete, valid JSON value.</exception>
    public static RestoreManifestIdentity? Read(Stream stream)
    {
        var window = ArrayPool<byte>.Shared.Rent(WindowBytes);

        try
        {
            var length = Fill(stream, window, 0, out var final);
            var start = window.AsSpan(0, length).StartsWith(Utf8ByteOrderMark) ? Utf8ByteOrderMark.Length : 0;
            var state = new JsonReaderState();
            var visitor = new IdentityVisitor();

            while (true)
            {
                var reader = new Utf8JsonReader(window.AsSpan(start, length - start), final, state);

                while (reader.Read())
                {
                    visitor.Visit(ref reader);
                }

                if (final)
                {
                    return visitor.Result;
                }

                state = reader.CurrentState;

                var unread = window.AsSpan(start + (int)reader.BytesConsumed, length - start - (int)reader.BytesConsumed);

                if (unread.Length == window.Length)
                {
                    // One token fills the whole window, so it cannot complete until the window grows.
                    var larger = ArrayPool<byte>.Shared.Rent(window.Length * 2);
                    unread.CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(window);
                    window = larger;
                }
                else
                {
                    unread.CopyTo(window);
                }

                length = Fill(stream, window, unread.Length, out final);
                start = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    /// <summary>
    /// Reads into <paramref name="window"/> from <paramref name="offset"/> until it is full or the
    /// stream ends, and returns how much of it now holds data.
    /// </summary>
    private static int Fill(Stream stream, byte[] window, int offset, out bool final)
    {
        var filled = offset;
        int read;

        while (filled < window.Length && (read = stream.Read(window, filled, window.Length - filled)) > 0)
        {
            filled += read;
        }

        final = filled < window.Length;
        return filled;
    }

    [Flags]
    private enum Field
    {
        None = 0,
        Version = 1,
        Project = 2,
        Restore = 4,
        ProjectPath = 8,
    }

    /// <summary>
    /// Follows the token stream and records the root's <c>version</c> and
    /// <c>project.restore.projectPath</c>, and nothing nested anywhere else.
    ///
    /// Depth decides where a property is: the root's own properties are at depth 1, the
    /// <c>project</c> object's at 2 and the <c>restore</c> object's at 3. A property at depth 3 is
    /// <c>restore</c>'s only while that object is open, so a <c>projectPath</c> inside some other
    /// object of the same depth is not mistaken for it.
    /// </summary>
    private struct IdentityVisitor
    {
        private Field _seen;
        private Field _awaitingValue;
        private bool _duplicated;
        private bool _inProject;
        private bool _inRestore;
        private int? _version;
        private string? _projectPath;

        public readonly RestoreManifestIdentity? Result =>
            _duplicated ? null : new RestoreManifestIdentity(_version, _projectPath);

        public void Visit(ref Utf8JsonReader reader)
        {
            var depth = reader.CurrentDepth;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _awaitingValue = depth switch
                {
                    1 when reader.ValueTextEquals("version"u8) => Field.Version,
                    1 when reader.ValueTextEquals("project"u8) => Field.Project,
                    2 when _inProject && reader.ValueTextEquals("restore"u8) => Field.Restore,
                    3 when _inRestore && reader.ValueTextEquals("projectPath"u8) => Field.ProjectPath,
                    _ => Field.None,
                };

                _duplicated |= (_seen & _awaitingValue) != Field.None;
                _seen |= _awaitingValue;
                return;
            }

            switch (_awaitingValue)
            {
                case Field.Version:
                    _version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version)
                        ? version
                        : null;
                    break;

                case Field.Project:
                    _inProject = reader.TokenType == JsonTokenType.StartObject;
                    break;

                case Field.Restore:
                    _inRestore = reader.TokenType == JsonTokenType.StartObject;
                    break;

                case Field.ProjectPath:
                    _projectPath = reader.TokenType == JsonTokenType.String ? ReadString(ref reader) : null;
                    break;
            }

            _awaitingValue = Field.None;

            // While project is open, the only object closing at depth 1 is project itself, and while
            // restore is open the only one closing at depth 2 is restore.
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                _inProject &= depth != 1;
                _inRestore &= depth != 2;
            }
        }

        /// <summary>
        /// The string value, or null if it is not valid UTF-8. Transcoding throws
        /// <see cref="InvalidOperationException"/> for that, which is a reason to refuse the manifest
        /// rather than a fault in reading it.
        /// </summary>
        private static string? ReadString(ref Utf8JsonReader reader)
        {
            try
            {
                return reader.GetString();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
