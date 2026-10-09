using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every copy is checked again immediately before it goes, by either route, and a copy that fails a
/// check stays on the disk with the check it failed (§7.4).
/// </summary>
public sealed class DuplicateRemovalCheckTests : DuplicateRemovalScene
{
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void AMarkedCopyGoesAndTheCopyKeptStays(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var report = Remove(marks, mode);

        Assert.Equal(RemovalCheck.Removed, Assert.Single(report.Copies).Check);
        Assert.False(File.Exists(copy.Path));
        Assert.True(File.Exists(kept.Path));
        Assert.True(report.Verification.Passed, report.Summary);
    }

    /// <summary>
    /// Bytes added to the end leave the first bytes, all a comparison of the length the search found
    /// would read, the same as the copy kept: only the check that the copy is still the file the
    /// search found stops it.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyThatChangedAfterTheSearchIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        using (var file = File.Open(copy.Path, FileMode.Append))
        {
            file.Write([1, 2, 3]);
        }

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.Changed, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void NothingGoesWhereTheCopyKeptChangedAfterTheSearch(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        File.SetLastWriteTimeUtc(kept.Path, kept.Modified.AddSeconds(1));

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.NoKeptCopy, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// A collision forced on the group: both files carry one checksum and differ in the middle, which
    /// neither their first nor their last block shows. The checksum groups, and never licenses the
    /// removal.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyWhoseBytesDifferIsNotRemovedThoughItsChecksumMatched(ExploreRemovalMode mode)
    {
        var content = Content();
        var other = content.ToArray();
        other[content.Length / 2] ^= 0xFF;
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), content));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), other));
        var marks = MarksWithChecksum(new ContentChecksum(ChecksumAlgorithm.XxHash128, new byte[16]), [kept, copy]);
        Mark(marks, copy);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.ContentDiffers, outcome.Check);
        Assert.Equal(other, File.ReadAllBytes(copy.Path));
    }

    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyHoldingANamedStreamTheCopyKeptLacksIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Write(Path.Combine(Documents, "a.bin"), Content());
        var copy = Write(Path.Combine(Downloads, "a.bin"), Content());
        File.WriteAllText(copy + ":notes", "written only on this copy");
        var marks = Marks([Found(kept), Found(copy)]);
        Mark(marks, marks.Groups[0].Group.Files[1]);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.StreamsDiffer, outcome.Check);
        Assert.Equal("written only on this copy", File.ReadAllText(copy + ":notes"));
    }

    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyWhoseNamedStreamHoldsOtherDataIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Write(Path.Combine(Documents, "a.bin"), Content());
        var copy = Write(Path.Combine(Downloads, "a.bin"), Content());
        File.WriteAllText(kept + ":notes", "kept notes");
        File.WriteAllText(copy + ":notes", "copy notes");
        var marks = Marks([Found(kept), Found(copy)]);
        Mark(marks, marks.Groups[0].Group.Files[1]);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.StreamsDiffer, outcome.Check);
        Assert.True(File.Exists(copy));
    }

    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyDifferingOnlyInWhereItWasDownloadedFromIsRemoved(ExploreRemovalMode mode)
    {
        var kept = Write(Path.Combine(Documents, "a.bin"), Content());
        var copy = Write(Path.Combine(Downloads, "a.bin"), Content());
        File.WriteAllText(kept + ":notes", "the same notes");
        File.WriteAllText(copy + ":notes", "the same notes");
        File.WriteAllText(copy + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var marks = Marks([Found(kept), Found(copy)]);
        Mark(marks, marks.Groups[0].Group.Files[1]);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.Removed, outcome.Check);
        Assert.False(File.Exists(copy));
    }

    /// <summary>
    /// <c>OFFLINE</c> is the one cloud attribute an ordinary file can be given: the copy went
    /// online-only after the search, and is not on this computer to compare.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyThatWentOnlineOnlyIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        File.SetAttributes(copy.Path, FileAttributes.Offline);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.OnlyInTheCloud, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// A stale group naming one file twice, by two spellings of its path: the copy marked and a copy
    /// the group would keep are one file. The kept copy is chosen by identity, never by path, so the
    /// file is not held as kept, nothing else can be, and nothing goes.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void TheSameFileReachedByTwoPathsIsNeverRemovedAgainstItself(ExploreRemovalMode mode)
    {
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var alias = copy with { Path = Path.Combine(Path.GetDirectoryName(copy.Path)!, "A.BIN"), Names = [Path.Combine(Path.GetDirectoryName(copy.Path)!, "A.BIN")] };
        var marks = Marks([copy, alias]);
        var plan = new PlannedGroup(marks.Groups[0].Group, [copy]);

        var report = Remover().Remove([plan], [], marks.Keeping, mode, CancellationToken.None);

        Assert.Equal(RemovalCheck.NoKeptCopy, Assert.Single(report.Copies).Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// §6.3: the path that opens the copy to compare and remove it reaches Windows in its extended
    /// form, and so does the path that describes it first. A deep tree would open either way on a
    /// machine that allows long paths, so only the form shows it.
    /// </summary>
    [Fact]
    public void ThePathsThatReachWindowsAreInTheirExtendedForm()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        List<string> opened = [];
        List<string> described = [];
        var files = new FileInformation(
            (extended, use) =>
            {
                described.Add(extended);
                return FileInformation.Open(extended, use);
            },
            FileInformation.ReadIdentity);

        var report = Remove(marks, ExploreRemovalMode.Permanent, open: (extended, use) =>
        {
            opened.Add(extended);
            return FileInformation.OpenHeld(extended, use);
        }, files: files);

        Assert.True(Assert.Single(report.Copies).Removed);
        Assert.Equal([LongPath.Extended(kept.Path), LongPath.Extended(copy.Path)], opened);
        Assert.Contains(LongPath.Extended(copy.Path), described);
        Assert.All(described, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }
}
