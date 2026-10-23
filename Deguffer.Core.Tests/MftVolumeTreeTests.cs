using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Tests;

/// <summary>
/// The parsed table the index answers from: what it keeps about each record, and what that costs.
/// </summary>
public class MftVolumeTreeTests
{
    private const uint Profile = 7;

    /// <summary>
    /// Every flag of a record shares one byte, so each must read back as it was set and leave its
    /// neighbours alone. A record carrying all of them and one carrying none, side by side, catch a
    /// flag read through the wrong bit as well as one that was never stored.
    /// </summary>
    [Fact]
    public void EachFlagReadsBackAsItWasSet()
    {
        var tree = new MftVolumeTree(20);

        // A store with every mark at once: the index leaves a store whatever reparse point it
        // carries, so the combination is one it has to read.
        tree.Set(16, new MftRecord(Profile, Size: null, IsDirectory: false, IsReparsePoint: true, 0, 0), "outlook.ost");
        tree.Set(17, new MftRecord(Profile, new ScanSize(4096, 4000), IsDirectory: false, IsReparsePoint: false, 0, 0), "a.tgz");
        tree.Set(18, new MftRecord(Profile, ScanSize.Zero, IsDirectory: true, IsReparsePoint: false, 0, 0), "cache");

        Assert.True(tree.IsMailStore(16));
        Assert.True(tree.IsReparsePoint(16));
        Assert.True(tree.SizeUnknown(16));
        Assert.False(tree.IsDirectory(16));
        Assert.True(tree.IsPresent(16));

        Assert.False(tree.IsMailStore(17));
        Assert.False(tree.IsReparsePoint(17));
        Assert.False(tree.SizeUnknown(17));
        Assert.False(tree.IsDirectory(17));
        Assert.True(tree.IsPresent(17));

        Assert.True(tree.IsDirectory(18));
        Assert.False(tree.IsMailStore(18));
        Assert.True(tree.IsPresent(18));

        // A slot no record filled is absent, which is what keeps it out of the child links.
        Assert.False(tree.IsPresent(19));
        Assert.False(tree.IsLinkable(19));
        Assert.True(tree.IsLinkable(17));
    }

    /// <summary>
    /// The tree is sized by the volume's record count, millions on a system volume, and kept for as
    /// long as the index is held, so a byte a record is megabytes. Its five flags are one byte
    /// between them: a <c>bool[]</c> each was four bytes a record more.
    /// </summary>
    [Fact]
    public void TheTreeCostsThirtySevenBytesARecord()
    {
        const int records = 1_000_000;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var tree = new MftVolumeTree(records);
        var perRecord = (GC.GetAllocatedBytesForCurrentThread() - before) / records;
        GC.KeepAlive(tree);

        // The parent's four bytes, eight each for the allocated size, the logical size and the
        // newest time, eight for a reference to the name a directory keeps, and one for the flags.
        Assert.Equal(37, perRecord);
    }
}
