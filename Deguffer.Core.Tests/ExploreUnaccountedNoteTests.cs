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
        Assert.Contains("Scan as administrator", ExploreUnaccountedNote.For(isElevated: false, Volume, Scanned));
        Assert.DoesNotContain("Scan as administrator", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned));
        Assert.Contains("even an administrator's scan cannot open", ExploreUnaccountedNote.For(isElevated: true, Volume, Scanned));
    }

    /// <summary>Everything else it can be made of is the same either way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothNamesTheCausesAnyScanLeaves(bool isElevated)
    {
        var note = ExploreUnaccountedNote.For(isElevated, Volume, Scanned);

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
            ExploreUnaccountedNote.For(isElevated: false, refused, Scanned));
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

        var note = ExploreUnaccountedNote.For(isElevated: true, stated, Scanned);

        Assert.Contains($"Restore points and shadow copies: {FreeSpace.Format(1_000)}.", note);
        Assert.Contains($"Reserved storage: {FreeSpace.Format(500)}.", note);
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

        var note = ExploreUnaccountedNote.For(isElevated: true, stated, Scanned);

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

        Assert.DoesNotContain("Restore points", ExploreUnaccountedNote.For(isElevated: true, counted, Scanned));
    }

    /// <summary>A part Windows states at nothing is not something the block can hold.</summary>
    [Fact]
    public void APartStatedAtNothingIsNotACause()
    {
        var none = Volume with { Hidden = new HiddenSpace(default, new ReservedStorage(Statement.Stated, 0)) };

        Assert.DoesNotContain("reserved storage", ExploreUnaccountedNote.For(isElevated: true, none, Scanned));
    }
}
