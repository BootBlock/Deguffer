using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// The record of what a run's removals left standing, which §5.6 reads as evidence that a removal went
/// inside a protected folder. No disk is touched here: the record compares paths and nothing else.
/// </summary>
public sealed class RunResidueTests
{
    private const string Scratch = @"C:\Users\testuser\AppData\Local\Temp";

    /// <summary>
    /// What a removal left alone is asked of that removal alone, and covers what is inside the entry
    /// as well as the entry. A removal that never entered a folder took nothing inside it.
    /// </summary>
    [Fact]
    public void WhatARemovalLeftAloneIsAnsweredForThatRemovalOnly()
    {
        var residue = new RunResidue();
        residue.RecordLeftAlone(Scratch, [$@"{Scratch}\kitprobe"]);
        residue.RecordLeftAlone(@"C:\Users\testuser\AppData\Local", [@"C:\Users\testuser\AppData\Local\Other"]);

        Assert.True(residue.LeftAlone(Scratch, $@"{Scratch}\kitprobe"));
        Assert.True(residue.LeftAlone($@"\\?\{Scratch}\", $@"{Scratch}\KITPROBE\closeup.py"));
        Assert.False(residue.LeftAlone(Scratch, $@"{Scratch}\other"));
        Assert.False(residue.LeftAlone(Scratch, Scratch));
        Assert.False(residue.LeftAlone(@"C:\Users\testuser\AppData\Local", $@"{Scratch}\kitprobe"));
    }

    /// <summary>
    /// Two removals of one folder answer separately. A later one that found an entry already gone,
    /// because the earlier one took it, records it as left alone, and that must not vouch for the
    /// earlier one, which recorded nothing.
    /// </summary>
    [Fact]
    public void EveryRemovalOfAFolderHasToHaveLeftTheEntryAlone()
    {
        var residue = new RunResidue();
        residue.RecordLeftAlone(Scratch, []);
        residue.RecordLeftAlone(Scratch, [$@"{Scratch}\kitprobe"]);

        Assert.False(residue.LeftAlone(Scratch, $@"{Scratch}\kitprobe"));
    }

    /// <summary>
    /// The steps of a plan that are apart record their removals at once, from threads of their own.
    /// Nothing any of them recorded is lost, which an unguarded set loses or corrupts under writers
    /// racing for it.
    /// </summary>
    [Fact]
    public void RemovalsRecordedAtOnceAreAllKept()
    {
        const int Removals = 64;
        const int Folders = 200;
        var residue = new RunResidue();

        Parallel.For(0, Removals, new ParallelOptions { MaxDegreeOfParallelism = 16 }, removal =>
        {
            // Every removal shares one root, so every writer adds to the same set.
            residue.Record(Scratch, [.. Enumerable.Range(0, Folders).Select(folder => $@"{Scratch}\r{removal}\f{folder}")]);
            residue.RecordLeftAlone(Scratch, [$@"{Scratch}\kept"]);
        });

        Assert.All(
            Enumerable.Range(0, Removals),
            removal => Assert.All(
                Enumerable.Range(0, Folders),
                folder => Assert.True(residue.Entered($@"{Scratch}\r{removal}\f{folder}"), $@"r{removal}\f{folder} was lost")));
        Assert.True(residue.LeftAlone(Scratch, $@"{Scratch}\kept\file.txt"));
        Assert.Equal([Scratch], residue.EnteredRoots);
    }

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
