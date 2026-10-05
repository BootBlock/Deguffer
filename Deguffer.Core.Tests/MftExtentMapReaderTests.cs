using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading where the table lives on a volume whose <c>$MFT</c> is fragmented enough for its run
/// list to outgrow record 0, so the rest of it is in extension records the list names.
///
/// <para>The table here is 32 records in four extents, described by three pieces: VCN 0 to 35 in
/// record 0, 36 to 43 in record 16 and 44 to 63 in record 19. Record 16 begins in the first extent
/// and ends in the second, and record 19 lies in the extent only record 16 describes.</para>
/// </summary>
public class MftExtentMapReaderTests
{
    private const long DataSize = 32 * FragmentedMftVolume.BytesPerRecord;

    private static readonly DataRun[] Layout =
        [new DataRun(1_000, 33), new DataRun(2_000, 3), new DataRun(3_000, 8), new DataRun(4_000, 20)];

    private static readonly MftPiece[] Pieces =
    [
        new MftPiece(Holder: 0, LowestVcn: 0, [Layout[0], Layout[1]]),
        new MftPiece(Holder: 16, LowestVcn: 36, [Layout[2]]),
        new MftPiece(Holder: 19, LowestVcn: 44, [Layout[3]]),
    ];

    [Fact]
    public void ReadsATableWhoseRunListSpansRecordZeroAndTwoExtensionRecords()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces);

        Assert.True(TryRead(volume, out var map));

        Assert.Equal(DataSize, map.DataSize);
        Assert.Equal(Layout, map.Runs);

        // The last record of the table is where only the last piece says it is.
        Assert.True(map.TryTranslate(63, out var physical, out _));
        Assert.Equal(4_019, physical);
    }

    /// <summary>A list grown too long for record 0 is kept in clusters of its own.</summary>
    [Fact]
    public void ReadsAListKeptOutsideRecordZero()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, listOutside: true);

        Assert.True(TryRead(volume, out var map));
        Assert.Equal(Layout, map.Runs);
    }

    /// <summary>
    /// Each extension record can only be found once the pieces before it are read, so the pieces are
    /// followed in the order of the clusters they describe, not the order the list gives them in.
    /// </summary>
    [Fact]
    public void FollowsThePiecesInClusterOrderWhateverOrderTheListGives()
    {
        var list = Pieces.Reverse().Select(Listed).ToList();
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, list);

        Assert.True(TryRead(volume, out var map));
        Assert.Equal(Layout, map.Runs);
    }

    /// <summary>
    /// Record 25 lies in the extent its own piece describes, so nothing read before it says where it
    /// is. The map cannot be completed, and the refusal is the reader's: no read was refused.
    /// </summary>
    [Fact]
    public void RefusesAnExtensionRecordOnlyItsOwnPieceCouldLocate()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, [Pieces[0], Pieces[1], Pieces[2] with { Holder = 25 }]);

        Assert.False(TryRead(volume, out _));
        Assert.False(volume.RefusedARead);
    }

    /// <summary>
    /// A gap leaves records with nowhere to be read from, and an overlap puts two places on one
    /// record. Either way the pieces do not describe one table.
    /// </summary>
    [Theory]
    [InlineData(45)]
    [InlineData(43)]
    public void RefusesPiecesThatDoNotMeet(long lastPieceStart)
    {
        var volume = new FragmentedMftVolume(
            Layout, DataSize, [Pieces[0], Pieces[1], Pieces[2] with { LowestVcn = lastPieceStart }]);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>
    /// The table is one byte longer than the clusters its pieces describe. A map that ends short is
    /// the partial index a refusal exists to prevent.
    /// </summary>
    [Fact]
    public void RefusesPiecesThatEndShortOfTheTable()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize + 1, Pieces);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>
    /// Record 16's piece says it describes VCN 36 to 43, which joins its neighbours, but its runs
    /// cover one cluster fewer or one more. Every cluster after it would be read from the wrong place.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void RefusesAPieceWhoseRunsDisagreeWithItsHeader(long clusters)
    {
        var volume = new FragmentedMftVolume(
            Layout,
            DataSize,
            [Pieces[0], Pieces[1] with { Runs = [new DataRun(3_000, clusters)], HighestVcn = 43 }, Pieces[2]]);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>
    /// An extension record has to name record 0, as record 0 is now, as its owner. One that names
    /// anything else belongs to another file, or to a record 0 since reused.
    /// </summary>
    [Theory]
    [InlineData(7, MftRecordBytes.Sequence)]
    [InlineData(0, MftRecordBytes.Sequence + 1)]
    public void RefusesAnExtensionRecordOwnedBySomethingElse(long owner, ushort sequence)
    {
        var volume = new FragmentedMftVolume(
            Layout, DataSize, Pieces, extensionBase: FragmentedMftVolume.Reference(owner, sequence));

        Assert.False(TryRead(volume, out _));
        Assert.False(volume.RefusedARead);
    }

    /// <summary>An extension record whose sequence number has moved on since the list named it was reused.</summary>
    [Fact]
    public void RefusesAnExtensionRecordReusedSinceTheListNamedIt()
    {
        var volume = new FragmentedMftVolume(
            Layout, DataSize, Pieces, extensionSequence: MftRecordBytes.Sequence + 1);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>A list naming record 0 by a sequence number it no longer has was caught mid-change.</summary>
    [Fact]
    public void RefusesAListNamingRecordZeroAsItWas()
    {
        var list = Pieces.Select(Listed).ToList();
        list[0] = list[0] with { Segment = FragmentedMftVolume.Reference(0, MftRecordBytes.Sequence + 1) };

        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, list);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>
    /// Record 16 holds a fourth piece the list does not name. The pieces still join, and still cover
    /// the table, but the list and the records disagree about where the table is.
    /// </summary>
    [Fact]
    public void RefusesAPieceTheListDoesNotName()
    {
        var unlisted = new MftPiece(Holder: 16, LowestVcn: 64, [new DataRun(5_000, 4)]);
        var volume = new FragmentedMftVolume(Layout, DataSize, [.. Pieces, unlisted], Pieces.Select(Listed).ToList());

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>The list names a second piece in record 19 that record 19 does not hold.</summary>
    [Fact]
    public void RefusesAListedPieceNoRecordHolds()
    {
        var list = Pieces.Select(Listed).Append(Listed(Pieces[2] with { LowestVcn = 54 })).ToList();
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, list);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>A list naming one piece twice is damage, though either line alone would be right.</summary>
    [Fact]
    public void RefusesAListNamingAPieceTwice()
    {
        var list = Pieces.Select(Listed).Append(Listed(Pieces[1])).ToList();
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, list);

        Assert.False(TryRead(volume, out _));
    }

    [Fact]
    public void RefusesAListThatCannotBeRead()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces, listOutside: true);
        volume.Lose(FragmentedMftVolume.ListCluster);

        Assert.False(TryRead(volume, out _));
    }

    /// <summary>The second half of record 16, the half in the second extent, is a bad sector.</summary>
    [Fact]
    public void RefusesAnExtensionRecordThatCannotBeRead()
    {
        var volume = new FragmentedMftVolume(Layout, DataSize, Pieces);
        volume.Lose(2_000);

        Assert.False(TryRead(volume, out _));
        Assert.True(volume.RefusedARead);
    }

    private static ListedAttribute Listed(MftPiece piece) =>
        new(0x80, FragmentedMftVolume.Reference(piece.Holder, MftRecordBytes.Sequence), piece.LowestVcn);

    private static bool TryRead(FragmentedMftVolume volume, out MftExtentMap map) =>
        MftExtentMapReader.TryRead(volume.Record0, FragmentedMftVolume.BytesPerCluster, volume.TryReadClusters, out map);
}
