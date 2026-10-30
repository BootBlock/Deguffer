using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>What the Explore page's status line says of a finished scan and of where the page is pointed.</summary>
public sealed class ExploreStatusTextTests
{
    private const string Refused = "Deguffer does not scan this drive.";

    /// <summary>
    /// The line says which figure the map draws, with the length beside it, and nothing else where a
    /// read of the file table counted the whole drive and no more than it has in use.
    /// </summary>
    [Fact]
    public void AFinishedScanSaysWhatTheFiguresAre()
    {
        var tree = Drive(
            new ExploreChild("film.mkv", IsDirectory: false, IsLink: false, Size: 0, Length: 9_000, Storage: FileStorage.CloudOnly),
            new ExploreChild("file", IsDirectory: false, IsLink: false, Size: 3_000));

        Assert.Equal(
            $"{FreeSpace.Format(3_000)} on disk accounted for, in files whose lengths add up to {FreeSpace.Format(12_000)}.",
            ExploreStatusText.Finished(ExploreScan.Fast(tree), new VolumeSpace(12_000, 6_000), scoped: false));
    }

    /// <summary>
    /// §7.1: a total that is a lower bound says so, naming what was scanned as the scan started: a
    /// drive or a folder.
    /// </summary>
    [Theory]
    [InlineData(false, "Some of this drive could not be read, so the totals are lower bounds.")]
    [InlineData(true, "Some of this folder could not be read, so the totals are lower bounds.")]
    public void ATotalThatIsALowerBoundSaysSo(bool scoped, string sentence)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var refused = builder.AddChildren(
            ExploreTreeBuilder.RootNode, [new ExploreChild("refused", IsDirectory: true, IsLink: false, Size: 0)]);

        Assert.DoesNotContain(
            "lower bounds",
            ExploreStatusText.Finished(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)), VolumeSpace.None, scoped));

        builder.MarkSizeUnknown(refused);

        Assert.Contains(
            sentence,
            ExploreStatusText.Finished(ExploreScan.Fast(builder.Build(ExploreChildOrder.BySize)), VolumeSpace.None, scoped));
    }

    /// <summary>
    /// A walk says what its figures leave out, and a scan that counted more than the drive has in use
    /// says so last, rather than showing a drive that only appears to add up.
    /// </summary>
    [Fact]
    public void AWalkSaysWhatItsFiguresLeaveOutAndWhereTheyCountTooMuch()
    {
        var volume = new VolumeSpace(12_000, 6_000);
        var said = ExploreStatusText.Finished(
            ExploreScan.Walked(Drive(new ExploreChild("linked", IsDirectory: false, IsLink: false, Size: 7_000)), FallbackReason.NotElevated),
            volume,
            scoped: false);

        Assert.Contains(ExploreRouteText.Sizing(ScanStrategy.ParallelEnumeration)!, said);
        Assert.EndsWith(ExploreUnaccountedNote.Overcount(volume, 7_000, ScanStrategy.ParallelEnumeration)!, said);
    }

    /// <summary>
    /// A choice that is not a folder on a disk says so, and a refusal of the target the page keeps is
    /// said after it: Scan stays greyed out, and the reason must not go with the choice.
    /// </summary>
    [Fact]
    public void AChoiceNotOnADiskKeepsTheStandingRefusalSaid()
    {
        Assert.Equal(PickedFolder.NotOnDisk, ExploreStatusText.NotOnDisk(null));
        Assert.Equal($"{PickedFolder.NotOnDisk} {Refused}", ExploreStatusText.NotOnDisk(Refused));
    }

    /// <summary>
    /// Pointed somewhere refused, the line says why. Pointed somewhere it will scan, the page takes
    /// back the refusal it stated, and nothing else: a scan's result or a selection's report written
    /// over it is not made untrue by a change of target.
    /// </summary>
    [Fact]
    public void ARefusalIsStatedAndTakenBackButNothingElseIs()
    {
        var prompt = StatusLine.Said(ExploreStatusText.Prompt);
        var other = StatusLine.Said("Something another part of the page said.");
        var removal = StatusLine.After("Moved 'old' to the Recycle Bin.", new VerificationResult { Checks = [] });

        Assert.Equal(StatusLine.Refused(Refused), ExploreStatusText.Retargeted(prompt, Refused));
        Assert.Equal(StatusLine.Refused(Refused), ExploreStatusText.Retargeted(other, Refused));
        Assert.Equal(prompt, ExploreStatusText.Retargeted(StatusLine.Refused(Refused), refusal: null));
        Assert.Same(other, ExploreStatusText.Retargeted(other, refusal: null));
        Assert.Same(removal, ExploreStatusText.Retargeted(removal, refusal: null));
    }

    /// <summary>
    /// Which sentence is the page's refusal is said by where it came from, not by its words: a line
    /// the page says of its own work stays, even where it reads exactly as a refusal did.
    /// </summary>
    [Fact]
    public void ARefusalIsKnownByWhereItCameFromNotByItsWords()
    {
        var told = StatusLine.Said(Refused);

        Assert.Same(told, ExploreStatusText.Retargeted(told, refusal: null));
    }

    private static ExploreTree Drive(params ExploreChild[] children)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, children);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
