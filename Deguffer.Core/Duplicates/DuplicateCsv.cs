using System.Buffers;
using System.Globalization;
using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>What saving the results came to, in the words the page shows.</summary>
public sealed record DuplicateCsvSaving(bool Saved, string Summary);

/// <summary>
/// The three things saving does to the disk, each given a path in the form it reaches Windows, so a
/// test can see that form (§6.3): a deep folder is written to whether or not the path carries the
/// extended prefix, so only the form can show it was given.
/// </summary>
/// <param name="Create">Creates a file that must not be there yet, for writing.</param>
/// <param name="Replace">Moves the first file over the second.</param>
/// <param name="Delete">Deletes a file.</param>
internal sealed record CsvFiles(Func<string, Stream> Create, Action<string, string> Replace, Action<string> Delete)
{
    public static CsvFiles Windows { get; } = new(
        path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None),
        (from, to) => File.Move(from, to, overwrite: true),
        File.Delete);
}

/// <summary>
/// A duplicate search's results as a CSV file the user names and places (§7.4): one row for each
/// copy, with its group's number, its path, its length in bytes, its last-modified time and, for a
/// group matched on content, the checksum and its algorithm. Deguffer writes no copy anywhere else.
///
/// <para><b>Read back as written.</b> Every field holding a comma, a quotation mark, a line break or
/// a space at either end is quoted, with its quotation marks doubled (RFC 4180), so a path holding
/// any of them reads back whole in a spreadsheet or a script. Lines end in CRLF, and the text is
/// UTF-8 with a byte order mark, which is how a spreadsheet knows a path is not in its own code
/// page.</para>
///
/// <para><b>Never half written under the name chosen.</b> The file is written beside the one chosen
/// under a name of its own and moved over it once whole, so a disk that fills or a drive pulled
/// partway leaves the file that was there, never a shorter list taken for the whole.</para>
/// </summary>
public static class DuplicateCsv
{
    private static readonly string[] Header = ["Group", "Path", "Size", "Modified", "Algorithm", "Checksum"];

    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    /// <summary>The CSV of <paramref name="groups"/>, numbered from 1 in the order given.</summary>
    public static void Write(TextWriter writer, IReadOnlyList<DuplicateGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(groups);

        writer.NewLine = "\r\n";
        WriteRow(writer, Header);

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var number = (g + 1).ToString(CultureInfo.InvariantCulture);
            var algorithm = group.Checksum is { } checksum ? checksum.Algorithm.Name() : string.Empty;
            var value = group.Checksum?.Hex ?? string.Empty;

            foreach (var copy in group.Files)
            {
                WriteRow(writer, [
                    number,
                    copy.Path,
                    copy.Length.ToString(CultureInfo.InvariantCulture),
                    copy.Modified.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                    algorithm,
                    value,
                ]);
            }
        }
    }

    /// <summary>
    /// Save the CSV of <paramref name="groups"/> as <paramref name="path"/>, the file the user chose,
    /// replacing it where it is there.
    /// </summary>
    public static Task<DuplicateCsvSaving> SaveAsync(string path, IReadOnlyList<DuplicateGroup> groups, CancellationToken ct) =>
        SaveAsync(path, groups, CsvFiles.Windows, ct);

    internal static async Task<DuplicateCsvSaving> SaveAsync(string path, IReadOnlyList<DuplicateGroup> groups, CsvFiles files, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(groups);

        // A name no file of the user's can have, so nothing they keep beside the export is written over.
        var written = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await Task.Run(
                () =>
                {
                    using (var stream = files.Create(LongPath.Extended(written)))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                    {
                        Write(writer, groups);
                    }

                    files.Replace(LongPath.Extended(written), LongPath.Extended(path));
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(written, files);

            return new DuplicateCsvSaving(false, $"The results were not saved: {ex.Message}");
        }

        var copies = groups.Sum(group => group.Files.Count);

        return new DuplicateCsvSaving(
            true,
            $"Saved {groups.Count:N0} {(groups.Count == 1 ? "group" : "groups")} and {copies:N0} copies as {Path.GetFileName(path)}.");
    }

    private static void WriteRow(TextWriter writer, ReadOnlySpan<string> fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            var field = fields[i];

            if (field.AsSpan().ContainsAny(NeedsQuoting) || (field.Length > 0 && (char.IsWhiteSpace(field[0]) || char.IsWhiteSpace(field[^1]))))
            {
                writer.Write('"');
                writer.Write(field.Replace("\"", "\"\"", StringComparison.Ordinal));
                writer.Write('"');
            }
            else
            {
                writer.Write(field);
            }
        }

        writer.WriteLine();
    }

    private static void TryDelete(string path, CsvFiles files)
    {
        try
        {
            files.Delete(LongPath.Extended(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file of this export's own name, which nothing reads; the user is told it was not saved.
        }
    }
}
