using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>What the reader is told about space in use that the scan did not count.</summary>
public sealed class ExploreUnaccountedNoteTests
{
    /// <summary>12,000 bytes with 6,000 free, beside a scan of 3,000, leaves 3,000 unaccounted.</summary>
    private static readonly VolumeSpace Volume = new(12_000, 6_000);

    private const long Scanned = 3_000;

    /// <summary>
    /// The fix an elevated scan brings is offered only to a scan without it. Offered to one that
    /// already has it, it sends the reader round in a circle.
    /// </summary>
    [Fact]
    public void ScanningAsAdministratorIsOfferedOnlyWhereItWouldHelp()
    {
        Assert.Contains("Scan as administrator", ExploreUnaccountedNote.For(isElevated: false, Volume, Scanned, ScanStrategy.ParallelEnumeration));
        Assert.DoesNotContain("Scan as administrator", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned, ScanStrategy.ParallelEnumeration));
        Assert.Contains("even an administrator's scan cannot open", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned, ScanStrategy.ParallelEnumeration));
    }

    /// <summary>Everything else it can be made of is the same either way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothNamesTheCausesAnyScanLeaves(bool isElevated)
    {
        var note = ExploreUnaccountedNote.For(isElevated, Volume, Scanned, ScanStrategy.ParallelEnumeration);

        Assert.StartsWith("Windows says this much of the drive is in use", note);
        Assert.Contains("System Volume Information", note);
        Assert.Contains("reserved storage", note);
        Assert.Contains("disk quota", note);
    }

    /// <summary>
    /// Unelevated, the shadow copy storage is a figure Windows will not give, and the note says what
    /// would give it rather than listing it as a cause with no figure.
    /// </summary>
    [Fact]
    public void AFigureWindowsRefusedSaysWhatWouldStateIt()
    {
        var refused = Volume with { Hidden = new HiddenSpace(ShadowStorage.Refused, default) };

        Assert.Contains(
            "Windows states their size only to an administrator",
            ExploreUnaccountedNote.For(isElevated: false, refused, Scanned, ScanStrategy.ParallelEnumeration));
    }

    /// <summary>
    /// A part drawn on its own is named as left out, with its figure, and no longer listed as
    /// something the block might hold.
    /// </summary>
    [Fact]
    public void APartDrawnOnItsOwnIsNamedAsLeftOutWithItsFigure()
    {
        var stated = Volume with
        {
            Hidden = new HiddenSpace(
                new ShadowStorage(Statement.Stated, 1_000, 1_000, 2_000),
                new ReservedStorage(Statement.Stated, 500)),
        };

        var note = ExploreUnaccountedNote.For(isElevated: true, stated, Scanned, ScanStrategy.ParallelEnumeration);

        Assert.Contains($"Restore points and shadow copies: {FreeSpace.Format(1_000)}.", note);
        Assert.Contains($"Reserved storage: {FreeSpace.Format(500)}.\nIt can include:", note);
        Assert.DoesNotContain("which Windows keeps in System Volume Information", note);
        Assert.DoesNotContain("(reserved storage)", note);
    }

    /// <summary>A part that did not fit is still given at Windows' figure, never at a difference.</summary>
    [Fact]
    public void APartThatDidNotFitIsGivenAtWindowsFigure()
    {
        var stated = Volume with
        {
            Hidden = new HiddenSpace(new ShadowStorage(Statement.Stated, 5_000, 5_000, 9_000), default),
        };

        var note = ExploreUnaccountedNote.For(isElevated: true, stated, Scanned, ScanStrategy.ParallelEnumeration);

        Assert.Contains($"System Volume Information: Windows states {FreeSpace.Format(5_000)}.", note);
        Assert.DoesNotContain("Not included", note);
    }

    /// <summary>Where the scan counted System Volume Information, the storage in it is not unaccounted.</summary>
    [Fact]
    public void StorageTheScanCountedIsNotListedAsACause()
    {
        var counted = Volume with
        {
            Hidden = new HiddenSpace(new ShadowStorage(Statement.Stated, 1_000, 1_000, 2_000), default),
            CountedSystemVolumeInformation = true,
        };

        Assert.DoesNotContain("Restore points", ExploreUnaccountedNote.For(isElevated: true, counted, Scanned, ScanStrategy.ParallelEnumeration));
    }

    /// <summary>A part Windows states at nothing is not something the block can hold.</summary>
    [Fact]
    public void APartStatedAtNothingIsNotACause()
    {
        var none = Volume with { Hidden = new HiddenSpace(default, new ReservedStorage(Statement.Stated, 0)) };

        Assert.DoesNotContain("reserved storage", ExploreUnaccountedNote.For(isElevated: true, none, Scanned, ScanStrategy.ParallelEnumeration));
    }

    /// <summary>
    /// The file table draws what each file occupies, whole clusters included, so rounding cannot be
    /// in the block beside it. A walk takes most files at their length and can leave it there.
    /// </summary>
    [Fact]
    public void NamesRoundingOnlyBesideAWalk()
    {
        Assert.Contains("Rounding", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned, ScanStrategy.ParallelEnumeration));
        Assert.DoesNotContain("Rounding", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned, ScanStrategy.MasterFileTable));
        Assert.Contains("file system's own records", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned, ScanStrategy.MasterFileTable));
    }

    /// <summary>
    /// #257: a drive holding a cloud library longer than the space in use. Drawn by length, the scan
    /// counts more than is in use, the block clamps to nothing, and the space it should have shown,
    /// the restore points among it, disappears. Drawn by space on disk, the block is there.
    /// </summary>
    [Fact]
    public void ACloudLibraryNoLongerHidesTheSpaceNoFolderAccountsFor()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode,
        [
            new ExploreChild("film.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 10_000, Storage: FileStorage.CloudOnly),
            new ExploreChild("plain.tgz", IsDirectory: false, IsLink: false, Size: 3_000),
        ]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        // 6,000 in use, and 13,000 long.
        Assert.True(tree.TotalLength > Volume.TotalBytes - Volume.FreeBytes);

        Assert.Equal(3_000, Volume.Parts(tree.TotalBytes).Unaccounted);
        Assert.Equal(0, Volume.Parts(tree.TotalBytes).Overcounted);
        Assert.Null(ExploreUnaccountedNote.Overcount(Volume, tree.TotalBytes, ScanStrategy.MasterFileTable));

        // What the length would have drawn: no block, and the page has to say why.
        Assert.Equal(0, Volume.Parts(tree.TotalLength).Unaccounted);
        Assert.Equal(7_000, Volume.Parts(tree.TotalLength).Overcounted);
    }

    /// <summary>
    /// Where a scan still counts more than is in use, the page says by how much and why, rather
    /// than drawing a picture that only appears to add up. The causes are the route's own.
    /// </summary>
    [Fact]
    public void SaysWhereTheScanCountedMoreThanIsInUse()
    {
        var walked = ExploreUnaccountedNote.Overcount(Volume, 7_000, ScanStrategy.ParallelEnumeration);
        var indexed = ExploreUnaccountedNote.Overcount(Volume, 7_000, ScanStrategy.MasterFileTable);

        Assert.StartsWith($"The scan counted {FreeSpace.Format(1_000)} more than Windows says is in use", walked);
        Assert.Contains("several names", walked);
        Assert.Contains("CompactOS", walked);
        Assert.DoesNotContain("several names", indexed);
        Assert.Contains("written while the scan ran", indexed);

        // Nothing to say about a folder, which has no volume drawn beside it.
        Assert.Null(ExploreUnaccountedNote.Overcount(VolumeSpace.None, 7_000, ScanStrategy.ParallelEnumeration));
    }
}
