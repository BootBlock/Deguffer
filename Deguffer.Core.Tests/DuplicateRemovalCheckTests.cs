using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

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
    /// An attribute changed since the search, which leaves the bytes and the times alone: only the
    /// check that the copy's attributes are as the search found them stops it.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyWhoseAttributesChangedAfterTheSearchIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        File.SetAttributes(copy.Path, File.GetAttributes(copy.Path) | FileAttributes.Hidden);

        var outcome = Assert.Single(Remove(marks, mode).Copies);

        Assert.Equal(RemovalCheck.Changed, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// Sharing refuses a write while the copies are compared, not a change to their attributes. The
    /// copy is hidden at the moment the copy kept is described again after the comparison, which is
    /// the last description before the removal, so only that final check stops it.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void ACopyChangedWhileItWasComparedIsNotRemoved(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var removing = false;
        var described = 0;
        var files = new FileInformation(
            FileInformation.Open,
            (SafeFileHandle handle, IdentityRoute route, out FileIdentity identity) =>
            {
                // After the copy to remove is opened: its own description, then the copy kept's
                // after the comparison, then the copy's own again.
                if (removing && ++described == 2)
                {
                    File.SetAttributes(copy.Path, File.GetAttributes(copy.Path) | FileAttributes.Hidden);
                }

                return FileInformation.ReadIdentity(handle, route, out identity);
            });

        var outcome = Assert.Single(Remove(marks, mode, files: files, open: (extended, use) =>
        {
            removing |= use is HeldFor.Removing;
            return FileInformation.OpenHeld(extended, use);
        }).Copies);

        Assert.True(described >= 2, "The copy kept was never described again after the comparison.");
        Assert.Equal(RemovalCheck.Changed, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// The shell refuses the extended-length prefix §6.3 requires everywhere else, so the Recycle
    /// Bin is handed the copy's path in its display form.
    /// </summary>
    [Fact]
    public void TheRecycleBinIsHandedThePathInTheFormTheShellParses()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var bin = FakeRecycleBin.MovingTo(Bin);

        Assert.True(Assert.Single(Remove(marks, ExploreRemovalMode.RecycleBin, bin: bin).Copies).Removed);
        Assert.Equal([copy.Path], bin.Paths);
        Assert.DoesNotContain(@"\?\", bin.Paths[0], StringComparison.Ordinal);
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
    /// A stale group naming one file twice, by two of its names: the copy marked and a copy the group
    /// would keep are one file. Each name is its own final path, so only the choice of the copy kept
    /// by identity, never by path, keeps the file from being held as kept while it goes. Nothing
    /// else can be kept, so nothing goes.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void TheSameFileReachedByTwoPathsIsNeverRemovedAgainstItself(ExploreRemovalMode mode)
    {
        var path = Write(Path.Combine(Downloads, "a.bin"), Content());
        var other = HardLink.To(path, Path.Combine(Documents, "a.bin"));
        var copy = Found(path);
        var alias = Found(other);
        Assert.Equal(copy.Identity, alias.Identity);
        var marks = Marks([copy, alias]);
        var plan = new PlannedGroup(marks.Groups[0].Group, [copy]);

        var report = Remover().Remove([plan], [], marks.Keeping, mode, CancellationToken.None);

        Assert.Equal(RemovalCheck.NoKeptCopy, Assert.Single(report.Copies).Check);
        Assert.True(File.Exists(path));
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
