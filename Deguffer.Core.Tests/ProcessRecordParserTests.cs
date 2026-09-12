using System.Buffers.Binary;
using Deguffer.Core.Memory;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The process table is a buffer of records whose positions and strings are only as trustworthy as
/// the offsets they were read from. These prove that each field comes from its own offset at both
/// pointer widths, and that every way a record can lie outside the buffer Windows returned stops the
/// walk rather than reading past it.
///
/// <para>Every name and figure here is invented. None comes from a real machine.</para>
/// </summary>
public sealed class ProcessRecordParserTests
{
    private static readonly ProcessRecord Parent = new(
        ProcessId: 1_204,
        ParentProcessId: 4,
        Name: "alpha.exe",
        CommitCharge: 31_000_000,
        PrivateWorkingSet: 22_000_000,
        CreationTime: 133_900_000_000_000_001);

    private static readonly ProcessRecord Child = new(
        ProcessId: 5_508,
        ParentProcessId: 1_204,
        Name: "beta.exe",
        CommitCharge: 47_000_000,
        PrivateWorkingSet: 39_000_000,
        CreationTime: 133_900_000_000_000_777);

    /// <summary>
    /// Every field of two records with different values in every field, so a field read from its
    /// neighbour's offset comes back wrong rather than coincidentally right.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void EveryFieldIsReadFromItsOwnOffset(int pointerSize)
    {
        var buffer = new ProcessTableBuffer(pointerSize).Add(Parent, threads: 3).Add(Child, threads: 0);
        var (data, _) = buffer.Build();

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, LayoutFor(pointerSize));

        Assert.True(parsed.Complete);
        Assert.Equal([Parent, Child], parsed.Records);
    }

    [Fact]
    public void AProcessWithNoNameIsReadWithAnEmptyOne()
    {
        var idle = Parent with { ProcessId = 0, ParentProcessId = 0, Name = string.Empty };
        var buffer = new ProcessTableBuffer(8).Add(idle).Add(Child);
        var (data, _) = buffer.Build();

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.True(parsed.Complete);
        Assert.Equal([idle, Child], parsed.Records);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void ATableCutShortInsideARecordKeepsWhatCameBeforeIt(int pointerSize)
    {
        var buffer = new ProcessTableBuffer(pointerSize).Add(Parent).Add(Child);
        var (data, offsets) = buffer.Build();

        var parsed = ProcessRecordParser.Parse(
            data.AsSpan(0, offsets[1] + buffer.RecordSize - 1), buffer.Base, LayoutFor(pointerSize));

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    /// <summary>
    /// Large enough to go negative as a signed offset, which is the step that would move the walk
    /// backwards out of the buffer rather than merely past its end.
    /// </summary>
    [Fact]
    public void ANextOffsetBeyondTheBufferStopsTheWalk()
    {
        var buffer = new ProcessTableBuffer(8).Add(Parent).Add(Child);
        var (data, _) = buffer.Build();

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ProcessTableBuffer.NextEntryOffset), uint.MaxValue - 15);

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    /// <summary>
    /// An offset of four would read the next record out of the middle of this one. At four, its name
    /// length lands on zeroed padding and its own next offset on the thread count of zero, so without
    /// the check that shifted record reads as a real, unnamed process ending a complete table.
    /// </summary>
    [Fact]
    public void ANextOffsetInsideTheRecordStopsTheWalk()
    {
        var buffer = new ProcessTableBuffer(8).Add(Parent, threads: 0).Add(Child);
        var (data, _) = buffer.Build();

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ProcessTableBuffer.NextEntryOffset), 4);

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    [Fact]
    public void ANameRunningPastTheEndStopsTheWalk()
    {
        var buffer = new ProcessTableBuffer(8).Add(Parent).Add(Child);
        var (data, offsets) = buffer.Build();

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offsets[1] + ProcessTableBuffer.NameLength), 0xFFFE);

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(1 << 20)]
    public void ANamePointingOutsideTheBufferStopsTheWalk(int fromStart)
    {
        var buffer = new ProcessTableBuffer(8).Add(Parent).Add(Child);
        var (data, offsets) = buffer.Build();

        buffer.WritePointer(data.AsSpan(offsets[1]), buffer.NameBuffer, (ulong)((long)buffer.Base + fromStart));

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    /// <summary>UTF-16 has no odd byte counts, so one says the length was read from the wrong place.</summary>
    [Fact]
    public void AnOddNameLengthStopsTheWalk()
    {
        var buffer = new ProcessTableBuffer(8).Add(Parent).Add(Child);
        var (data, offsets) = buffer.Build();

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offsets[1] + ProcessTableBuffer.NameLength), 7);

        var parsed = ProcessRecordParser.Parse(data, buffer.Base, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Equal([Parent], parsed.Records);
    }

    [Fact]
    public void ABufferTooShortForOneRecordIsNotATable()
    {
        var parsed = ProcessRecordParser.Parse(new byte[new ProcessTableBuffer(8).RecordSize - 1], 0x1000, ProcessRecordLayout.Wide);

        Assert.False(parsed.Complete);
        Assert.Empty(parsed.Records);
    }

    private static ProcessRecordLayout LayoutFor(int pointerSize) =>
        pointerSize == 8 ? ProcessRecordLayout.Wide : ProcessRecordLayout.Narrow;
}
