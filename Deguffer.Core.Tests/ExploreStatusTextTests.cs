using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>What the Explore page's status line says of where the page is pointed.</summary>
public sealed class ExploreStatusTextTests
{
    private const string Refused = "Deguffer does not scan this drive.";

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
}
