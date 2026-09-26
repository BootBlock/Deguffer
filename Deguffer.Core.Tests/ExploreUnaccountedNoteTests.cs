using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>What the reader is told about space in use that the scan did not count.</summary>
public sealed class ExploreUnaccountedNoteTests
{
    /// <summary>
    /// The fix an elevated scan brings is offered only to a scan without it. Offered to one that
    /// already has it, it sends the reader round in a circle.
    /// </summary>
    [Fact]
    public void ScanningAsAdministratorIsOfferedOnlyWhereItWouldHelp()
    {
        Assert.Contains("Scan as administrator", ExploreUnaccountedNote.For(isElevated: false));
        Assert.DoesNotContain("Scan as administrator", ExploreUnaccountedNote.For(isElevated: true));
        Assert.Contains("even an administrator's scan cannot open", ExploreUnaccountedNote.For(isElevated: true));
    }

    /// <summary>Everything else it can be made of is the same either way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothNamesTheCausesAnyScanLeaves(bool isElevated)
    {
        var note = ExploreUnaccountedNote.For(isElevated);

        Assert.StartsWith("Windows says this much of the drive is in use", note);
        Assert.Contains("System Volume Information", note);
        Assert.Contains("disk quota", note);
    }
}
