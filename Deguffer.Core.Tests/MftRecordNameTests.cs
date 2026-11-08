using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a record's names are read: the best of them and each of its other links beside it, all as the
/// UTF-16 the record holds, and into a string only by a caller that keeps one.
/// </summary>
public sealed class MftRecordNameTests
{
    private const uint Number = 40;

    /// <summary>
    /// Almost every long name carries an 8.3 alias beside it, and building a volume index keeps the
    /// name of a directory and of nothing else. A string for each name of each record would be
    /// several million thrown away on a system volume, so a parse makes none: the alias is ranked as
    /// it sits, and the winner is handed on as it sits.
    ///
    /// <para>Measured against a record whose one name is a single letter rather than against zero,
    /// so that what a parse allocates for anything other than its names, which an unoptimised build
    /// does, is not counted against them.</para>
    ///
    /// <para>The two are parsed in turn and compared round by round, and the median difference is
    /// what must be zero. The count is of everything the test's thread allocates, and the runtime now
    /// and then allocates a few bytes on it during a parse that are not the parse's own: sixteen,
    /// once in 200,000 parses, when measured. Compared as two totals, one such allocation in either
    /// loop fails the test. A string per name lands in every round.</para>
    /// </summary>
    [Fact]
    public void ParsingAFileAllocatesNothingForItsNames()
    {
        const int rounds = 1_000;

        var bare = new RepeatedParse(
            RecordOf(new MftFixture().AddFile(Number, MftRecord.RootRecordNumber, "a", allocated: 4096, logical: 4000)),
            "a");
        var named = new RepeatedParse(
            RecordOf(new MftFixture().AddFileWithAnAlias(
                Number, MftRecord.RootRecordNumber, "a long file name.bin", "ALONGF~1.BIN", allocated: 4096, logical: 4000)),
            "a long file name.bin");
        var differences = new long[rounds];

        // Once each first, so that compiling them is not measured.
        bare.Allocated();
        named.Allocated();

        for (var i = 0; i < rounds; i++)
        {
            differences[i] = named.Allocated() - bare.Allocated();
        }

        Array.Sort(differences);
        Assert.Equal(0, differences[rounds / 2]);
    }

    /// <summary>
    /// NTFS does not require a name to be valid UTF-16, and a directory listing returns what it
    /// stored. A name decoded through an encoding would have its unpaired surrogate replaced, and a
    /// path built from it would name nothing on the disk.
    /// </summary>
    [Fact]
    public void HandsOnANameWithAnUnpairedSurrogateAsNtfsStoredIt()
    {
        const string name = "cache\uD800.bin";
        var stored = RecordOf(new MftFixture().AddFile(Number, MftRecord.RootRecordNumber, name, allocated: 4096, logical: 4000));

        Assert.True(Parsed(stored, new byte[stored.Length], out var handed));
        Assert.Equal(name, handed.ToString());
    }

    /// <summary>
    /// A file's names that are not 8.3 aliases are each a link, and the parse hands on every one
    /// beside the name it places the file by, so each folder naming the file can count it. Here the
    /// alias sits first, the name the file is placed by second, and the other link last, in another
    /// folder. The alias is no link: a listing never shows it.
    /// </summary>
    [Fact]
    public void HandsOnEachOtherLinkOfAFileAndNeverItsAlias()
    {
        const uint Other = 30;

        var stored = RecordOf(new MftFixture().AddHardLinkedFile(
            Number,
            [(MftRecord.RootRecordNumber, "a long file name.bin"), (Other, "elsewhere.bin")],
            allocated: 4096,
            logical: 4000,
            alias: "ALONGF~1.BIN"));

        Assert.Equal(
            MftParseOutcome.Parsed, MftRecordParser.Parse(stored, Number, out var parsed, out var name, out var otherLinks, out _, out _));
        Assert.Equal("a long file name.bin", name.ToString());
        Assert.Equal(MftRecord.RootRecordNumber, parsed.ParentRecordNumber);
        Assert.Equal([new MftLink(Other, "elsewhere.bin")], Decoded(otherLinks));
    }

    /// <summary>A file with one name and its alias has no other link.</summary>
    [Fact]
    public void HandsOnNoOtherLinkForAFileWithOneNameAndItsAlias()
    {
        var stored = RecordOf(new MftFixture().AddFileWithAnAlias(
            Number, MftRecord.RootRecordNumber, "a long file name.bin", "ALONGF~1.BIN", allocated: 4096, logical: 4000));

        Assert.Equal(MftParseOutcome.Parsed, MftRecordParser.Parse(stored, Number, out _, out _, out var otherLinks, out _, out _));
        Assert.Equal(0, otherLinks.Count);
        Assert.Empty(Decoded(otherLinks));
    }

    /// <summary>
    /// A system volume's component store links each of its files into several folders, and a volume
    /// index keeps the name of a link only where it names a mail store. So placing a file with several
    /// links in the tree makes no string for any of their names: the parse hands them on as they sit,
    /// and the tree reads each where it sits.
    ///
    /// <para>Measured as <see cref="ParsingAFileAllocatesNothingForItsNames"/> is, against the same file
    /// whose links are each named by a single letter, so the queue entry each link costs the tree is
    /// counted on both sides and only the names can differ.</para>
    /// </summary>
    [Fact]
    public void PlacingAFileWithSeveralLinksAllocatesNothingForTheirNames()
    {
        const int rounds = 1_000;

        var bare = new RepeatedPlacement(RecordOf(new MftFixture().AddHardLinkedFile(
            Number,
            [(MftRecord.RootRecordNumber, "a"), (30, "b"), (31, "c"), (32, "d")],
            allocated: 4096,
            logical: 4000)));
        var named = new RepeatedPlacement(RecordOf(new MftFixture().AddHardLinkedFile(
            Number,
            [(MftRecord.RootRecordNumber, "a long file name.bin"), (30, "another long name.bin"), (31, "a third long name.bin"), (32, "and a fourth one.bin")],
            allocated: 4096,
            logical: 4000,
            alias: "ALONGF~1.BIN")));
        var differences = new long[rounds];

        bare.Allocated();
        named.Allocated();

        for (var i = 0; i < rounds; i++)
        {
            differences[i] = named.Allocated() - bare.Allocated();
        }

        Array.Sort(differences);
        Assert.Equal(0, differences[rounds / 2]);
    }

    /// <summary>One record parsed over and over, each time into a fresh copy of what it stored.</summary>
    private sealed class RepeatedParse(byte[] stored, string name)
    {
        private readonly byte[] _record = new byte[stored.Length];

        /// <summary>
        /// What one parse allocates on this thread, which must hand on the record's name.
        /// </summary>
        public long Allocated()
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = Parsed(stored, _record, out var handed);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(parsed);
            Assert.True(handed.SequenceEqual(name));
            return allocated;
        }
    }

    /// <summary>
    /// One record parsed and placed in a fresh tree over and over, as building a volume index does.
    /// </summary>
    private sealed class RepeatedPlacement(byte[] stored)
    {
        private const int Records = 64;

        private readonly byte[] _record = new byte[stored.Length];

        /// <summary>
        /// What one parse and placement allocate on this thread, which must place the file under each
        /// of its four names.
        /// </summary>
        public long Allocated()
        {
            var tree = new MftVolumeTree(Records);
            stored.CopyTo(_record);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var outcome = MftRecordParser.Parse(_record, Number, out var parsed, out var name, out var otherLinks, out _, out _);
            tree.Set(Number, in parsed, name, otherLinks);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(MftParseOutcome.Parsed, outcome);
            tree.PlaceLinks();
            Assert.Equal(3, tree.NodeCount - tree.Count);
            return allocated;
        }
    }

    /// <summary>The bytes of record <see cref="Number"/> in <paramref name="fixture"/>'s table.</summary>
    private static byte[] RecordOf(MftFixture fixture)
    {
        using var source = fixture.Build();
        var record = new byte[source.BytesPerRecord];

        Assert.Equal(1, source.ReadBatch(Number, record));
        return record;
    }

    /// <summary>
    /// Parse a fresh copy of <paramref name="stored"/> in <paramref name="record"/>, because a parse
    /// runs the fixup in place, and say whether it parsed and what name it handed on.
    /// </summary>
    private static bool Parsed(byte[] stored, byte[] record, out ReadOnlySpan<char> name)
    {
        stored.CopyTo(record);
        return MftRecordParser.Parse(record, Number, out _, out name, out _, out _, out _) == MftParseOutcome.Parsed;
    }

    /// <summary>Each link <paramref name="otherLinks"/> hands on, copied out to compare.</summary>
    private static List<MftLink> Decoded(MftOtherLinks otherLinks)
    {
        var decoded = new List<MftLink>();

        foreach (var link in otherLinks)
        {
            decoded.Add(new MftLink(link.ParentRecordNumber, link.Name.ToString()));
        }

        return decoded;
    }
}
