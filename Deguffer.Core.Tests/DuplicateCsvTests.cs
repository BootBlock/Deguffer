using System.Globalization;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.VisualBasic.FileIO;

namespace Deguffer.Core.Tests;

/// <summary>
/// A search's results saved as a CSV file the user names (§7.4): one row a copy, read back whole by
/// an independent RFC 4180 reader, however hostile its path, and never half written under the name
/// chosen.
/// </summary>
public sealed class DuplicateCsvTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private int _files;

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Every path a row can hold reads back as itself: a comma, a quotation mark (which Windows
    /// refuses in a name but NTFS stores where another system wrote one), line breaks, spaces at
    /// either end, a semicolon, letters outside any code page, and a path longer than
    /// <c>MAX_PATH</c>, whole.
    /// </summary>
    [Fact]
    public async Task TheCsvRoundTripsHostilePathsAndALongPathWhole()
    {
        var deep = @"C:\Users\testuser\" + string.Join('\\', Enumerable.Repeat(new string('d', 60), 6)) + @"\deep.bin";
        string[] paths =
        [
            @"C:\Users\testuser\Photos, 2024\a.jpg",
            "C:\\Users\\testuser\\\"quoted\" name.jpg",
            "C:\\Users\\testuser\\two\r\nlines.jpg",
            @"C:\Users\testuser\ leading and trailing .jpg ",
            @"C:\Users\testuser\semi;colon.jpg",
            @"C:\Users\testuser\Фото\写真 🎞.jpg",
            deep,
        ];
        Assert.True(deep.Length > 260);
        var groups = new[]
        {
            Group(ChecksumAlgorithm.Sha256, paths[..4]),
            Group(checksum: null, paths[4..]),
        };
        var saved = Path.Combine(_temp.Path, "results.csv");

        var saving = await DuplicateCsv.SaveAsync(saved, groups, default);

        Assert.True(saving.Saved, saving.Summary);
        var rows = Parse(saved);
        Assert.Equal(["Group", "Path", "Size", "Modified", "Algorithm", "Checksum"], rows[0]);
        Assert.Equal(paths, rows.Skip(1).Select(row => row[1]));
        Assert.All(rows, row => Assert.Equal(6, row.Length));
    }

    /// <summary>
    /// Each row carries its group's number in the order given, its length, its last-modified time
    /// to the tick in UTC, and the checksum with the name other tools print its algorithm under; a
    /// group not matched on content leaves both empty.
    /// </summary>
    [Fact]
    public void EachRowCarriesItsGroupSizeTimeAndChecksum()
    {
        var content = Group(ChecksumAlgorithm.Sha256, @"C:\a.bin", @"C:\b.bin");
        var named = Group(checksum: null, @"C:\c.bin", @"C:\d.bin");
        using var text = new StringWriter(CultureInfo.InvariantCulture);

        DuplicateCsv.Write(text, [content, named]);

        var rows = ParseText(text.ToString());
        Assert.Equal(["1", "1", "2", "2"], rows.Skip(1).Select(row => row[0]));
        Assert.Equal(content.Files[0].Length.ToString(CultureInfo.InvariantCulture), rows[1][2]);
        Assert.Equal(content.Files[0].Modified, DateTime.Parse(rows[1][3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(DateTimeKind.Utc, DateTime.Parse(rows[1][3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Kind);
        Assert.Equal(["SHA-256", content.Checksum!.Value.Hex], rows[2][4..]);
        Assert.Equal(["", ""], rows[3][4..]);
    }

    /// <summary>
    /// The file is UTF-8 with a byte order mark and CRLF line ends, which is how a spreadsheet reads
    /// a path outside its own code page correctly.
    /// </summary>
    [Fact]
    public async Task TheFileIsUtf8WithAByteOrderMarkAndCrlfLines()
    {
        var saved = Path.Combine(_temp.Path, "results.csv");

        await DuplicateCsv.SaveAsync(saved, [Group(checksum: null, @"C:\Фото\a.jpg", @"C:\b.jpg")], default);

        var bytes = File.ReadAllBytes(saved);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = File.ReadAllText(saved);
        Assert.Contains("Фото", text, StringComparison.Ordinal);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// A file already at the name chosen is replaced, and nothing else in its folder is touched: not
    /// even a file named as a careless temporary name would have been, and no file of the save's own
    /// is left behind.
    /// </summary>
    [Fact]
    public async Task SavingReplacesTheFileChosenAndTouchesNothingBesideIt()
    {
        var folder = _temp.CreateDirectory("Exports");
        var saved = Path.Combine(folder, "results.csv");
        File.WriteAllText(saved, "an older export");
        File.WriteAllText(saved + ".tmp", "the user's own file");

        var saving = await DuplicateCsv.SaveAsync(saved, [Group(checksum: null, @"C:\a.jpg", @"C:\b.jpg")], default);

        Assert.True(saving.Saved);
        Assert.Equal("Saved 1 group and 2 copies as results.csv.", saving.Summary);
        Assert.StartsWith("Group,Path", File.ReadAllText(saved), StringComparison.Ordinal);
        Assert.Equal("the user's own file", File.ReadAllText(saved + ".tmp"));
        Assert.Equal(["results.csv", "results.csv.tmp"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order());
    }

    /// <summary>
    /// §6.3, asserted by the form of every path the save hands Windows, both the file it writes and
    /// the one it moves that over, in a folder deeper than <c>MAX_PATH</c>. A deep folder is written
    /// to whether or not the path carries the prefix, so only the form shows it was given.
    /// </summary>
    [Fact]
    public async Task EveryPathTheSaveHandsWindowsIsInItsExtendedForm()
    {
        var deep = _temp.Path;

        while (deep.Length <= 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        var saved = Path.Combine(deep, "results.csv");
        List<string> handed = [];
        var files = new CsvFiles(
            path =>
            {
                handed.Add(path);
                return CsvFiles.Windows.Create(path);
            },
            (from, to) =>
            {
                handed.Add(from);
                handed.Add(to);
                CsvFiles.Windows.Replace(from, to);
            },
            CsvFiles.Windows.Delete);

        var saving = await DuplicateCsv.SaveAsync(saved, [Group(checksum: null, @"C:\a.jpg", @"C:\b.jpg")], files, default);

        Assert.True(saving.Saved, saving.Summary);
        Assert.Equal(3, handed.Count);
        Assert.All(handed, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
        Assert.Equal(LongPath.Extended(saved), handed[2]);
    }

    /// <summary>A file the save cannot write is reported as not saved, and leaves nothing behind.</summary>
    [Fact]
    public async Task ASaveThatCannotBeWrittenSaysSoAndLeavesNothing()
    {
        var folder = _temp.CreateDirectory("Exports");
        var saved = Path.Combine(folder, "results.csv");
        using var held = new FileStream(saved, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var saving = await DuplicateCsv.SaveAsync(saved, [Group(checksum: null, @"C:\a.jpg", @"C:\b.jpg")], default);

        Assert.False(saving.Saved);
        Assert.StartsWith("The results were not saved: ", saving.Summary, StringComparison.Ordinal);
        Assert.Equal(["results.csv"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
    }

    private DuplicateGroup Group(ChecksumAlgorithm? checksum, params string[] paths)
    {
        DuplicateCandidate[] copies =
        [
            .. paths.Select(path => new DuplicateCandidate(
                new FileIdentity(1, (UInt128)(++_files)),
                path,
                Path.GetFileName(path),
                [path],
                1,
                1_234_567,
                1_236_992,
                new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1_234_567),
                FileStorage.Plain,
                LocationRole.Search)),
        ];

        return new DuplicateGroup(
            checksum is null ? MatchCriteria.Name : MatchCriteria.Content,
            copies[0].Length,
            checksum is { } algorithm ? new ContentChecksum(algorithm, new byte[ChecksumAlgorithms.DigestBytes(algorithm)!.Value]) : null,
            copies);
    }

    /// <summary>The rows of the file at <paramref name="path"/>.</summary>
    private static List<string[]> Parse(string path)
    {
        using var parser = new TextFieldParser(LongPath.Extended(path), System.Text.Encoding.UTF8);

        return Rows(parser);
    }

    private static List<string[]> ParseText(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv));

        return Rows(parser);
    }

    private static List<string[]> Rows(TextFieldParser parser)
    {
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;

        List<string[]> rows = [];

        while (!parser.EndOfData)
        {
            rows.Add(parser.ReadFields()!);
        }

        return rows;
    }
}
