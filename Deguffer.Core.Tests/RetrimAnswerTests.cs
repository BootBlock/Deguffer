using Deguffer.Core.Retrim;

namespace Deguffer.Core.Tests;

/// <summary>
/// How Windows' answers to <c>MSFT_Volume.Optimize</c> are read and reported, and what it is asked
/// to do.
/// </summary>
public sealed class RetrimAnswerTests
{
    /// <summary>
    /// Only <c>ReTrim</c> is switched on, and every other input is given and switched off: Deguffer
    /// never defragments, and an unset switch is the provider's to read as it likes.
    /// </summary>
    [Fact]
    public void OnlyTheRetrimIsAskedForAndDefragmentingIsSwitchedOff()
    {
        Assert.Equal(["ReTrim"], RetrimParameters.All.Where(p => p.Value).Select(p => p.Name));
        Assert.Contains(("Defrag", false), RetrimParameters.All);
        Assert.Equal(
            ["ReTrim", "Analyze", "Defrag", "SlabConsolidate", "TierOptimize"],
            RetrimParameters.All.Select(p => p.Name));
    }

    [Theory]
    [InlineData(0, RetrimOutcome.Retrimmed)]
    [InlineData(1, RetrimOutcome.NotSupported)]
    [InlineData(40001, RetrimOutcome.NeedsElevation)]
    [InlineData(2, RetrimOutcome.Failed)]
    [InlineData(3, RetrimOutcome.Failed)]
    [InlineData(55555, RetrimOutcome.Failed)]
    public void AReturnValueIsReadByWhatWindowsDocumentsAndAnswers(int returnValue, RetrimOutcome expected)
    {
        Assert.Equal(expected, RetrimAnswer.FromReturnValue(returnValue).Outcome);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), RetrimOutcome.NeedsElevation)]
    [InlineData(unchecked((int)0x80041003), RetrimOutcome.NeedsElevation)]
    [InlineData(unchecked((int)0x8004100E), RetrimOutcome.Unanswered)]
    public void AFailedCallIsAnElevationRefusalOnlyWhereItSaysSo(int hr, RetrimOutcome expected)
    {
        Assert.Equal(new RetrimAnswer(expected, hr), RetrimAnswer.FromError(hr));
    }

    [Theory]
    [InlineData(RetrimOutcome.Retrimmed, 0, "Windows retrimmed C:.")]
    [InlineData(RetrimOutcome.NotSupported, 0, "Windows said C: does not support a retrim.")]
    [InlineData(RetrimOutcome.Failed, 3, "Windows could not retrim C:. It answered \"Timeout\" (code 3).")]
    [InlineData(
        RetrimOutcome.Failed,
        7,
        "Windows could not retrim C:. It answered \"This command is not supported on x86 running in x64 environment\" (code 7).")]
    [InlineData(RetrimOutcome.Failed, 55555, "Windows could not retrim C:. It answered with code 55555, which it does not document.")]
    [InlineData(
        RetrimOutcome.Unanswered,
        unchecked((int)0x8004100E),
        "Windows' storage management did not answer for C: (error 0x8004100E), so it was not retrimmed.")]
    [InlineData(RetrimOutcome.NotOffered, 0, "C: is no longer a drive a retrim is offered on, so it was not retrimmed.")]
    public void EachResultIsReportedInWindowsOwnTerms(RetrimOutcome outcome, int code, string expected)
    {
        Assert.Equal(expected, RetrimReport.Result(@"C:\", new RetrimAnswer(outcome, code)));
    }

    /// <summary>
    /// The offer names each drive with what it freed and why, says a retrim never defragments, and,
    /// only where Deguffer is not an administrator, that accepting it restarts Deguffer as one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOfferSaysWhatEachDriveGetsBackAndWhatAcceptingDoes(bool isElevated)
    {
        IReadOnlyList<RetrimCandidate> candidates =
        [
            new(new Safety.LocalVolume(@"C:\", DriveType.Fixed, Safety.VolumeReadiness.Ready), 5L << 30, RetrimBasis.ThinlyProvisioned),
            new(new Safety.LocalVolume(@"D:\", DriveType.Fixed, Safety.VolumeReadiness.Ready), 2L << 30, RetrimBasis.SolidState),
        ];

        var offer = RetrimReport.Offer(candidates, isElevated);

        Assert.Contains("The clean freed 5.0 GB on C:, which is thinly provisioned.", offer);
        Assert.Contains("The clean freed 2.0 GB on D:, which is solid state.", offer);
        Assert.Contains("Deguffer never defragments", offer);
        Assert.Equal(!isElevated, offer.Contains("restarts as administrator", StringComparison.Ordinal));
        Assert.Equal(
            isElevated ? "Retrim 2 drives" : "Restart as administrator and retrim",
            RetrimReport.Action(candidates, isElevated));
        Assert.Equal("Retrim C:", RetrimReport.Action([candidates[0]], isElevated: true));
    }

    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"C:\Mounts\Data\", @"C:\Mounts\Data\")]
    public void ADriveRootIsNamedByItsLetterAndAFolderMountWhole(string root, string expected)
    {
        Assert.Equal(expected, RetrimReport.Shown(root));
    }
}
