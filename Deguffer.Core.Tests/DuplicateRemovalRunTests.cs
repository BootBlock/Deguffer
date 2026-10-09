using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a duplicate removal does at the last moment, what the Recycle Bin received, what the run
/// takes, and the §5.6 evidence that it took no more (§7.4).
/// </summary>
public sealed class DuplicateRemovalRunTests : DuplicateRemovalScene
{
    /// <summary>
    /// Another program tries to delete the copy kept while the copy is going, at the last moment
    /// before each route removes it: the copy kept is held refusing every other program's delete.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public void TheCopyKeptSurvivesAnAttemptToDeleteItDuringTheRemoval(ExploreRemovalMode mode)
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var refused = false;

        void DeleteTheCopyKept()
        {
            try
            {
                File.Delete(kept.Path);
            }
            catch (IOException)
            {
                refused = true;
            }
        }

        var report = Remove(
            marks,
            mode,
            bin: FakeRecycleBin.MovingTo(Bin, _ => DeleteTheCopyKept()),
            delete: handle =>
            {
                DeleteTheCopyKept();
                return HandleDeletion.Delete(handle);
            });

        Assert.True(Assert.Single(report.Copies).Removed);
        Assert.True(refused);
        Assert.True(File.Exists(kept.Path));
    }

    /// <summary>
    /// After the comparison, the compared copy is moved aside and another file put at its path. A
    /// permanent removal deletes through the handle that was compared, so the file now at the path
    /// stays and the compared copy goes.
    /// </summary>
    [Fact]
    public void AFilePutAtTheCopysPathAfterTheComparisonIsNotWhatAPermanentRemovalDeletes()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var aside = Path.Combine(Downloads, "aside.bin");
        byte[] newcomer = [7, 7, 7];

        var report = Remove(marks, ExploreRemovalMode.Permanent, delete: handle =>
        {
            File.Move(copy.Path, aside);
            File.WriteAllBytes(copy.Path, newcomer);
            return HandleDeletion.Delete(handle);
        });

        Assert.True(Assert.Single(report.Copies).Removed);
        Assert.Equal(newcomer, File.ReadAllBytes(copy.Path));
        Assert.False(File.Exists(aside));
    }

    [Fact]
    public void ACopyTheBinRefusesIsNeverDeletedOutright()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var outcome = Assert.Single(Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.Refusing("Too large for the bin.")).Copies);

        Assert.Equal(RemovalCheck.BinRefused, outcome.Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>
    /// The compared copy is moved aside as the shell is asked, and another file put at its path, which
    /// the bin then takes. What the bin received is not the file compared, so the run stops there,
    /// names it, and the next group's copy is never reached.
    /// </summary>
    [Fact]
    public void ABinItemThatIsNotTheComparedCopyStopsTheRunAndIsNamed()
    {
        var firstKept = Found(Write(Path.Combine(Documents, "a.bin"), Content(seed: 1)));
        var first = Found(Write(Path.Combine(Downloads, "a.bin"), Content(seed: 1)));
        var secondKept = Found(Write(Path.Combine(Documents, "b.bin"), Content(seed: 2)));
        var second = Found(Write(Path.Combine(Downloads, "b.bin"), Content(seed: 2)));
        var marks = Marks([firstKept, first], [secondKept, second]);
        Mark(marks, first, second);
        var aside = Path.Combine(Documents, "aside.bin");

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.MovingTo(Bin, path =>
        {
            if (!File.Exists(aside))
            {
                File.Move(path, aside);
                File.WriteAllBytes(path, [7, 7, 7]);
            }
        }));

        var stopped = Assert.Single(report.Copies, copy => copy.Check == RemovalCheck.BinReceivedAnother);
        Assert.Same(stopped, report.StoppedAt);
        Assert.Contains(stopped.Copy.Path, stopped.Message, StringComparison.Ordinal);
        Assert.Contains(report.Copies, copy => copy.Check == RemovalCheck.NotReached && File.Exists(copy.Copy.Path));
        Assert.True(File.Exists(aside));
    }

    /// <summary>A bin that moves the copy and does not say where leaves nothing to identify, so the run stops there.</summary>
    [Fact]
    public void AMoveTheBinDoesNotPlaceStopsTheRun()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: new FakeRecycleBin());

        Assert.Equal(RemovalCheck.BinUnconfirmed, Assert.Single(report.Copies).Check);
        Assert.NotNull(report.StoppedAt);
    }

    /// <summary>
    /// A folder that is case-sensitive holds the copy <c>a.bin</c> and a different file, <c>A.bin</c>.
    /// A bin that takes both must fail §5.6, which compares every name exactly: compared without regard
    /// to case, the removed <c>a.bin</c> would stand in for the lost <c>A.bin</c>.
    /// </summary>
    [Fact]
    public void LosingAFileWhoseNameDiffersOnlyInCaseFromTheRemovedCopyFailsTheCheck()
    {
        var folder = CaseSensitiveFolder.Create(Path.Combine(Downloads, "Exact"));
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(folder, "a.bin"), Content()));
        var twin = Write(Path.Combine(folder, "A.bin"), Content(seed: 3));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.MovingTo(Bin, _ => File.Delete(twin)));

        Assert.True(Assert.Single(report.Copies).Removed);
        var failed = Assert.Single(report.Verification.Failures);
        Assert.Equal(folder, failed.Subject);
        Assert.Contains("'A.bin'", failed.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference copy that is not the one held as kept is lost while the group's copy goes: §5.6
    /// finds it missing by its file ID.
    /// </summary>
    [Fact]
    public void AReferenceCopyLostDuringTheRemovalFailsTheCheck()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var reference = Found(Write(Path.Combine(Documents, "Reference", "a.bin"), Content()), LocationRole.Reference);
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, reference, copy]);
        Mark(marks, copy);

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.MovingTo(Bin, _ => File.Delete(reference.Path)));

        Assert.True(Assert.Single(report.Copies).Removed);
        var failed = Assert.Single(report.Verification.Failures);
        Assert.Equal(reference.Path, failed.Subject);
        Assert.StartsWith("MISSING", failed.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The copy could not be marked while its group could keep nothing, so the confirmation did not
    /// list it. By the removal the copy kept counts again and the old mark stands, and still nothing
    /// goes that the user was not shown.
    /// </summary>
    [Fact]
    public void NothingGoesThatTheConfirmationDidNotList()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var confirmation = RemovalConfirmation.For(marks, KeepingWithDocumentsCleaned(kept), ExploreRemovalMode.Permanent, _ => null);
        Assert.Empty(confirmation.Copies);

        var (plan, dropped) = DuplicateRemover.Plan(marks.Groups, confirmation.Copies, marks.Keeping);
        var report = Remover().Remove(plan, dropped, marks.Keeping, confirmation.Mode, CancellationToken.None);

        Assert.Empty(report.Copies);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>The confirmation listed the copy; by the removal its group can keep nothing, so it stays, and says why.</summary>
    [Fact]
    public void AListedCopyWhoseMarkNoLongerStandsStaysAndSaysWhy()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);
        var confirmation = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.Permanent, _ => null);

        var later = KeepingWithDocumentsCleaned(kept);
        var (plan, dropped) = DuplicateRemover.Plan(marks.Groups, confirmation.Copies, later);
        var report = Remover().Remove(plan, dropped, later, confirmation.Mode, CancellationToken.None);

        Assert.Equal(RemovalCheck.MarkNoLongerStands, Assert.Single(report.Copies).Check);
        Assert.True(File.Exists(copy.Path));
    }

    /// <summary>The keeping rule with a Storage clean that deletes what is in the folder holding <paramref name="kept"/>.</summary>
    private CopyKeeping KeepingWithDocumentsCleaned(DuplicateCandidate kept)
    {
        _cleans.Add(new StorageClean(CleanedPlace.Whole(Path.GetDirectoryName(kept.Path)!), "A cache"));
        var keeping = MarksWithChecksum(null, [kept]).Keeping;
        _cleans.Clear();

        return keeping;
    }
}
