using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The record of what a run's removals left standing, which §5.6 reads as evidence that a removal went
/// inside a protected folder. No disk is touched here: the record compares paths and nothing else.
/// </summary>
public sealed class RunResidueTests
{
    private const string Scratch = @"C:\Users\testuser\AppData\Local\Temp";

    [Fact]
    public void EveryFolderBetweenWhatStayedAndTheRootWasEntered()
    {
        var residue = new RunResidue();
        residue.Record(Scratch, [$@"{Scratch}\live\session\work"]);

        Assert.True(residue.Entered($@"{Scratch}\live"));
        Assert.True(residue.Entered($@"{Scratch}\live\session"));
        Assert.True(residue.Entered($@"{Scratch}\live\session\work"));
    }

    /// <summary>
    /// The negative. The root is the removal's own subject, the folders above it were never walked,
    /// a sibling it left nothing in was not entered, and nothing below what stayed was left standing.
    /// </summary>
    [Fact]
    public void NeitherTheRootNorAnythingOutsideWhatStayedWasEntered()
    {
        var residue = new RunResidue();
        residue.Record(Scratch, [$@"{Scratch}\live\session", Scratch]);

        Assert.False(residue.Entered(Scratch));
        Assert.False(residue.Entered(@"C:\Users\testuser\AppData\Local"));
        Assert.False(residue.Entered($@"{Scratch}\other"));
        Assert.False(residue.Entered($@"{Scratch}\live\session\work"));
    }

    /// <summary>
    /// The bound belongs to the removal that recorded it. A narrower removal recording a folder first
    /// must not stop a broader one recording the folders between the two roots, and the record does
    /// not rely on a removal listing every folder above what it could not take.
    /// </summary>
    [Fact]
    public void AFolderANarrowerRemovalRecordedDoesNotStopABroaderOneClimbingPastIt()
    {
        var residue = new RunResidue();
        residue.Record($@"{Scratch}\live", [$@"{Scratch}\live\session\work"]);
        residue.Record(Scratch, [$@"{Scratch}\live\session\work"]);

        Assert.True(residue.Entered($@"{Scratch}\live"));
    }

    /// <summary>
    /// A removal's paths arrive in the extended-length form §6.3 requires, and a protected path may be
    /// spelled with a trailing separator or in a different case. They are one path.
    /// </summary>
    [Fact]
    public void ComparesEveryPathInOneFormWhateverFormItArrivedIn()
    {
        var residue = new RunResidue();
        residue.Record($@"\\?\{Scratch}\", [$@"\\?\{Scratch}\LIVE\session"]);

        Assert.True(residue.Entered($@"{Scratch}\live\"));
    }

    [Fact]
    public void AFreshRecordHasNothingInIt()
    {
        Assert.False(new RunResidue().Entered($@"{Scratch}\live"));
    }
}
