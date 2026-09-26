using System.Buffers;
using System.Text;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What a conversation says about itself that a reader needs to choose it: where, when and what.</summary>
/// <param name="Project">
/// The folder the session was started in, as Claude Code recorded it. The project's identity: the name
/// of the folder the conversation is filed under cannot be, because Claude Code's encoding of it loses
/// information.
/// </param>
/// <param name="Started">When the session was started, where a line in its head records it.</param>
/// <param name="Title">
/// The title Claude Code shows for it, or null where it has none. A title the user gave the session
/// wins over one Claude Code generated, among the titles that were read.
/// </param>
public sealed record ClaudeCodeConversation(string Project, DateTimeOffset? Started, string? Title);

/// <summary>
/// Reads a conversation's project, start and title from the head and the tail of its transcript, and
/// nothing else.
///
/// <para><b>The format is internal to Claude Code and changes between versions</b>, so nothing here is
/// depended on beyond four fields, and every read fails closed: a transcript this cannot place in a
/// project is not described at all, and its caller does not offer it.</para>
///
/// <para><b>What was measured, over 1,405 transcripts on one machine.</b> Every one recorded <c>cwd</c>
/// by its fifth line. A title arrives as repeated <c>ai-title</c> lines, a later one superseding an
/// earlier, and as a <c>custom-title</c> line where the user named the session. Claude Code writes its
/// titles again as a conversation grows, so the tail holds the title it shows now: the last title was
/// within 64 KB of the end in 1,309 of 1,390 that had one, and so was the user's own title in both
/// conversations that had one. The first title came within 620 KB of the start in every case, and the
/// head is read for a title only where the tail holds none. Fifteen had no title at all, so a
/// conversation without one is ordinary.</para>
///
/// <para><b>Nobody's conversation is read.</b> A line is parsed only where it may carry one of the four
/// fields, and only those fields are taken from it. The last line of a transcript records the last
/// prompt typed, word for word, and it is never a title.</para>
///
/// <para><b>§6.3.</b> The file is opened through <see cref="LongPath.Extended"/>. What comes back is a
/// title, a folder and a date, so no test can tell a long path from a short one here: the removals are
/// covered by the §6.3 assertions on the removal seam, and these reads are not.</para>
/// </summary>
internal static class ClaudeCodeTranscriptReader
{
    /// <summary>Far enough into a transcript to find its first title in every one measured.</summary>
    private const int HeadBytes = 1024 * 1024;

    /// <summary>Where Claude Code writes a session's latest title, in nearly every one measured.</summary>
    private const int TailBytes = 64 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>Longer than any title Claude Code writes. A longer one is cut rather than shown whole.</summary>
    private const int MaximumTitleLength = 200;

    private static ReadOnlySpan<byte> TitleMarker => "-title"u8;

    /// <summary>
    /// What <paramref name="path"/> records about its session, or null where it cannot be read or does
    /// not record the folder it was held in.
    /// </summary>
    public static ClaudeCodeConversation? Read(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(
                LongPath.Extended(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var titles = new Titles();

            // The tail first: it is where the title Claude Code shows now is, and a title found there makes
            // the head's titles unnecessary to parse.
            ReadTail(stream, titles, ct);

            var head = ReadHead(stream, titles, ct);

            return head.Project is { } project
                ? new ClaudeCodeConversation(project, head.Started, titles.Best)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone, locked or refused. Not described, so not offered.
            return null;
        }
        catch (InvalidOperationException)
        {
            // A field holding an unpaired surrogate, which parses and cannot be read as a string. Not a
            // transcript this can describe, so not offered.
            return null;
        }
    }

    private static void ReadTail(FileStream stream, Titles titles, CancellationToken ct)
    {
        // One byte before the tail as well, so that a tail starting exactly at a line's start keeps that line:
        // the byte is then the line break before it.
        var length = stream.Length;
        var start = Math.Max(0, length - TailBytes - 1);
        var buffer = ArrayPool<byte>.Shared.Rent(TailBytes + 1);

        try
        {
            stream.Seek(start, SeekOrigin.Begin);
            var read = stream.ReadAtLeast(buffer.AsSpan(0, (int)(length - start)), (int)(length - start), throwOnEndOfStream: false);
            var span = buffer.AsSpan(0, read);

            // A tail that starts inside a line starts with the end of it, which is not a line.
            if (start > 0)
            {
                var first = span.IndexOf((byte)'\n');
                span = first < 0 ? [] : span[(first + 1)..];
            }

            while (!span.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();

                var end = span.IndexOf((byte)'\n');
                var line = end < 0 ? span : span[..end];
                span = end < 0 ? [] : span[(end + 1)..];

                if (line.IndexOf(TitleMarker) >= 0)
                {
                    titles.Take(line, fromTail: true);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (string? Project, DateTimeOffset? Started) ReadHead(FileStream stream, Titles titles, CancellationToken ct)
    {
        string? project = null;
        DateTimeOffset? started = null;

        // Whether everything this reads the head for has been found.
        bool Take(ReadOnlySpan<byte> line)
        {
            if (project is null || started is null)
            {
                Place(line, ref project, ref started);
            }

            if (!titles.FoundInTail && line.IndexOf(TitleMarker) >= 0)
            {
                titles.Take(line, fromTail: false);
            }

            return project is not null && started is not null && titles.FoundInTail;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(HeadBytes);

        try
        {
            stream.Seek(0, SeekOrigin.Begin);

            var filled = 0;
            var lineStart = 0;

            while (filled < HeadBytes)
            {
                ct.ThrowIfCancellationRequested();

                var read = stream.Read(buffer, filled, Math.Min(ChunkBytes, HeadBytes - filled));

                if (read == 0)
                {
                    // The last line of the file, where no line break follows it. A line cut off by the
                    // budget below is never read, because it is not a line.
                    if (lineStart < filled)
                    {
                        Take(buffer.AsSpan(lineStart, filled - lineStart));
                    }

                    break;
                }

                var scanFrom = filled;
                filled += read;

                int end;
                while ((end = Array.IndexOf(buffer, (byte)'\n', scanFrom, filled - scanFrom)) >= 0)
                {
                    var line = buffer.AsSpan(lineStart, end - lineStart);
                    lineStart = scanFrom = end + 1;

                    if (Take(line))
                    {
                        return (project, started);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (project, started);
    }

    /// <summary>The first <c>cwd</c> and the first <c>timestamp</c> in the head, from whichever lines carry them.</summary>
    private static void Place(ReadOnlySpan<byte> line, ref string? project, ref DateTimeOffset? started)
    {
        using var record = Parse(line);

        if (record is null)
        {
            return;
        }

        project ??= BoundedJsonFile.StringProperty(record.RootElement, "cwd") is { Length: > 0 } cwd ? cwd : null;

        if (started is null
            && BoundedJsonFile.StringProperty(record.RootElement, "timestamp") is { } text
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
        {
            started = when;
        }
    }

    /// <summary>A line as a JSON object, or null where it is not one: cut off, or being written.</summary>
    private static JsonDocument? Parse(ReadOnlySpan<byte> line) => BoundedJsonFile.Parse(line.ToArray());

    /// <summary>The titles seen so far, latest last, with the user's own preferred.</summary>
    private sealed class Titles
    {
        private string? _custom;
        private string? _generated;

        public bool FoundInTail { get; private set; }

        public string? Best => _custom ?? _generated;

        /// <summary>
        /// Take a title from a line that may be one. A line from the tail wins over any from the head,
        /// because it was written later.
        /// </summary>
        public void Take(ReadOnlySpan<byte> line, bool fromTail)
        {
            using var record = Parse(line);

            if (record is null)
            {
                return;
            }

            var (field, custom) = BoundedJsonFile.StringProperty(record.RootElement, "type") switch
            {
                "custom-title" => ("customTitle", true),
                "ai-title" => ("aiTitle", false),
                _ => (null, false),
            };

            if (field is null || Clean(BoundedJsonFile.StringProperty(record.RootElement, field)) is not { } title)
            {
                return;
            }

            if (custom)
            {
                _custom = title;
            }
            else
            {
                _generated = title;
            }

            FoundInTail |= fromTail;
        }

        private static string? Clean(string? title)
        {
            if (title is null)
            {
                return null;
            }

            var cleaned = new StringBuilder(Math.Min(title.Length, MaximumTitleLength));

            foreach (var c in title)
            {
                var ch = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;

                if (ch == ' ' && (cleaned.Length == 0 || cleaned[^1] == ' '))
                {
                    continue;
                }

                cleaned.Append(ch);
            }

            var text = cleaned.ToString().TrimEnd();

            return text.Length switch
            {
                0 => null,
                > MaximumTitleLength => string.Concat(text.AsSpan(0, MaximumTitleLength - 1), "…"),
                _ => text,
            };
        }
    }
}
