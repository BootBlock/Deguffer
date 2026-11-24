using Deguffer.App.Converters;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Storage page's result card draws a clean's safety check: a glyph whose shape says the
/// verdict, a colour that backs the shape up, and the copy of the run in plain view only where the
/// check did not pass outright.
/// </summary>
public sealed class SafetyCheckTests
{
    [Theory]
    [InlineData(RunVerdict.AllSurvived, SafetyCheckGlyph.Passed)]
    [InlineData(RunVerdict.Unverified, SafetyCheckGlyph.Incomplete)]
    [InlineData(RunVerdict.RemovedFromOutside, SafetyCheckGlyph.Incomplete)]
    [InlineData(RunVerdict.VerificationFailed, SafetyCheckGlyph.Failed)]
    public void EachVerdictTakesTheGlyphForWhatItMeans(RunVerdict verdict, string glyph) =>
        Assert.Equal(glyph, SafetyCheckGlyph.Of(verdict));

    /// <summary>
    /// A pass, a check that could not be completed and a failure are three shapes, so the outcome
    /// reads without its colour (§6.5).
    /// </summary>
    [Fact]
    public void APassAnIncompleteCheckAndAFailureAreThreeShapes()
    {
        string[] glyphs =
        [
            SafetyCheckGlyph.Of(RunVerdict.AllSurvived),
            SafetyCheckGlyph.Of(RunVerdict.Unverified),
            SafetyCheckGlyph.Of(RunVerdict.VerificationFailed),
        ];

        Assert.Equal(3, glyphs.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(RunVerdict.AllSurvived, "SafetyCheckPassedGlyph", "SafetyCheckHeading")]
    [InlineData(RunVerdict.Unverified, "SafetyCheckIncompleteGlyph", "SafetyCheckHeading")]
    [InlineData(RunVerdict.RemovedFromOutside, "SafetyCheckIncompleteGlyph", "SafetyCheckHeading")]
    [InlineData(RunVerdict.VerificationFailed, "SafetyCheckFailedGlyph", "SafetyCheckFailedHeading")]
    public void EachVerdictTakesItsOwnColour(RunVerdict verdict, string glyphStyle, string headingStyle)
    {
        Assert.Equal(glyphStyle, SafetyCheckGlyphStyleConverter.KeyFor(verdict));
        Assert.Equal(headingStyle, SafetyCheckHeadingStyleConverter.KeyFor(verdict));
    }

    /// <summary>A verdict nobody drew is never drawn as a pass.</summary>
    [Fact]
    public void AVerdictNobodyDrewIsNeverDrawnAsAPass()
    {
        var unnamed = (RunVerdict)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => SafetyCheckGlyph.Of(unnamed));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafetyCheckGlyphStyleConverter.KeyFor(unnamed));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafetyCheckHeadingStyleConverter.KeyFor(unnamed));
    }

    /// <summary>
    /// The copy of the run is a link in plain view wherever the check did not pass outright, and
    /// waits behind the menu on a pass, where a link would suggest something went wrong.
    /// </summary>
    [Theory]
    [InlineData(RunVerdict.AllSurvived, false)]
    [InlineData(RunVerdict.Unverified, true)]
    [InlineData(RunVerdict.RemovedFromOutside, true)]
    [InlineData(RunVerdict.VerificationFailed, true)]
    public void TheDiagnosticLinkFollowsTheVerdict(RunVerdict verdict, bool linked)
    {
        var check = new SafetyCheckViewModel();

        check.Show(Outcome(verdict), [], "report");

        Assert.Equal(linked, check.OffersDiagnosticsLink);
        Assert.Equal(linked, check.ShowsDiagnosticsRow);
        Assert.Equal(!linked, check.OffersDiagnosticsMenu);
    }

    /// <summary>
    /// On a pass the row opens only to say whether a copy made from the menu went, and the next run
    /// takes that word away, so a new verdict never says an earlier run's report was copied.
    /// </summary>
    [Fact]
    public void ACopyFromTheMenuIsSaidUntilTheNextRun()
    {
        var check = new SafetyCheckViewModel();
        check.Show(Outcome(RunVerdict.AllSurvived), [], "report");

        check.Copied(null);

        Assert.True(check.ShowsDiagnosticsRow);
        Assert.Equal("Copied. Paste it into a GitHub issue.", check.CopyResult);

        check.Show(Outcome(RunVerdict.AllSurvived), [], "the next report");

        Assert.False(check.ShowsDiagnosticsRow);
        Assert.Empty(check.CopyResult);
    }

    [Fact]
    public void ARefusedCopyIsSaidInItsOwnWords()
    {
        var check = new SafetyCheckViewModel();
        check.Show(Outcome(RunVerdict.VerificationFailed), [], "report");

        check.Copied("Another program is using the clipboard.");

        Assert.Equal("Another program is using the clipboard.", check.CopyResult);
    }

    /// <summary>With no run on the card, nothing about a run is offered.</summary>
    [Fact]
    public void NothingIsOfferedWithNoRunOnTheCard()
    {
        var check = new SafetyCheckViewModel();
        check.Show(Outcome(RunVerdict.VerificationFailed), [], "report");

        check.Clear();

        Assert.Null(check.Outcome);
        Assert.False(check.OffersDiagnosticsLink);
        Assert.False(check.OffersDiagnosticsMenu);
        Assert.False(check.ShowsDiagnosticsRow);
        Assert.Empty(check.Diagnostics);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("6 file(s) changed too recently to remove.", true)]
    public void AnEmptyDetailTakesNoRoom(string detail, bool shown)
    {
        var check = new SafetyCheckViewModel();

        check.Show(new RunOutcome(RunVerdict.AllSurvived, detail, "Checked 1 item that had to survive.", false), [], "report");

        Assert.Equal(shown, check.HasDetail);
    }

    private static RunOutcome Outcome(RunVerdict verdict) =>
        new(verdict, "Cleaned.", "Checked 1 item that had to survive.", Cancelled: false);
}
