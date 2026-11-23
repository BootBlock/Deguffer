using System.Runtime.CompilerServices;
using Deguffer.Core.Scanning.Mft;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the first pass hands on what it gathered from every parse thread: in record order, the same
/// order every time, and without the cost of sorting or growing the values themselves.
/// </summary>
public sealed class MftByRecordTests
{
    private const int Reads = 3;

    /// <summary>
    /// A damaged table can have two owners name one extension record, so the second pass gathers
    /// values under one number. They come back in the order they were added, whatever the sort
    /// made of them. Enough of them, out of order, that the sort is not the insertion sort it uses
    /// for a short run, which keeps ties in order on its own.
    /// </summary>
    [Fact]
    public void HandsValuesBackInRecordOrderWithTiesInTheOrderAdded()
    {
        var added = Enumerable.Range(0, 500).Select(i => (Number: (long)(i * 7919 % 13), Value: i)).ToList();
        var byRecord = new MftByRecord<int>();

        foreach (var (number, value) in added)
        {
            byRecord.Add(number, value);
        }

        var sorted = byRecord.InRecordOrder();
        var expected = added.OrderBy(a => a.Number).ToList();

        Assert.Equal(expected.Select(e => e.Value), Enumerable.Range(0, sorted.Count).Select(i => sorted[i]));
        Assert.Equal(expected.Select(e => e.Number), Enumerable.Range(0, sorted.Count).Select(sorted.Number));
        Assert.Equal(expected.Select(e => e.Value), sorted);
    }

    /// <summary>
    /// The first pass keeps every extension record that holds something the scan needs, and a
    /// system volume keeps tens of thousands. Kept in one list, they grew by doubling to one large
    /// array of 200-byte values, about two and a half times what they hold. What the hand-off
    /// allocates beyond the parse itself is now each value once, its record number and its sorted
    /// key, and the part-filled chunks.
    /// </summary>
    [Fact]
    public void KeepsManyExtensionRecordsForLittleMoreThanTheirOwnSize()
    {
        const int Records = 20_000;
        const int First = 100;
        const int Chunks = 4 * 85_000;

        var template = MftRecordBytes.Compose(
            isDirectory: false,
            MftFixture.Reference(20),
            MftRecordBytes.Sequence,
            MftRecordBytes.BytesPerRecord,
            t => MftAttributeBytes.WriteNonResidentData(t, allocated: 8192, logical: 8000, startVirtualCluster: 0));
        var record = new byte[template.Length];
        var kept = 0;

        var parsing = ThreadAllocation.Least(() =>
        {
            for (var i = 0; i < Records; i++)
            {
                template.CopyTo(record, 0);
                MftRecordParser.Parse(record, First + i, out _, out _, out _, out _, out _);
            }
        }, Reads);

        var keeping = ThreadAllocation.Least(() =>
        {
            var handOff = new MftRecordHandOff(First + Records, bitmap: null, static (_, _, in _, _, _) => true, lists: null);

            for (var i = 0; i < Records; i++)
            {
                template.CopyTo(record, 0);
                handOff.Take(First + i, record);
            }

            kept = handOff.Extensions().Count;
        }, Reads);

        Assert.Equal(Records, kept);
        Assert.InRange(
            keeping - parsing,
            0,
            ((long)Records * (Unsafe.SizeOf<MftExtensionRecord>() + sizeof(long) + Unsafe.SizeOf<MftByRecord<MftExtensionRecord>.Key>())) + Chunks);
    }
}
