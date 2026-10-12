using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How a record's name is read: only the best of its names, as the UTF-16 the record holds, and
/// into a string only by a caller that keeps it.
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
    /// </summary>
    [Fact]
    public void ParsingAFileAllocatesNothingForItsNames()
    {
        var bare = RecordOf(new MftFixture().AddFile(Number, MftRecord.RootRecordNumber, "a", allocated: 4096, logical: 4000));
        var named = RecordOf(new MftFixture().AddFileWithAnAlias(
            Number, MftRecord.RootRecordNumber, "a long file name.bin", "ALONGF~1.BIN", allocated: 4096, logical: 4000));

        Assert.Equal(AllocatedParsing(bare, "a"), AllocatedParsing(named, "a long file name.bin"));
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
    /// What a thousand parses of <paramref name="stored"/> allocate, after one to compile them, each
    /// of which must hand on <paramref name="name"/>.
    /// </summary>
    private static long AllocatedParsing(byte[] stored, string name)
    {
        const int parses = 1_000;
        var record = new byte[stored.Length];

        Assert.True(Parsed(stored, record, out _));

        var handedOn = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < parses; i++)
        {
            if (Parsed(stored, record, out var handed) && handed.SequenceEqual(name))
            {
                handedOn++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(parses, handedOn);
        return allocated;
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
        return MftRecordParser.Parse(record, Number, out _, out name, out _, out _) == MftParseOutcome.Parsed;
    }
}
