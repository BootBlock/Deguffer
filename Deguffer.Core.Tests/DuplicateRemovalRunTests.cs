using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using Microsoft.Win32.SafeHandles;

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
    /// names it, and the next group's copy is never reached. On a drive that keeps no file numbers,
    /// as FAT32 and exFAT do not, the handle the copy was compared through still tells them apart.
    /// </summary>
    [Theory]
    [InlineData("NTFS")]
    [InlineData("FAT32")]
    public void ABinItemThatIsNotTheComparedCopyStopsTheRunAndIsNamed(string fileSystem)
    {
        DuplicateCandidate On(DuplicateCandidate copy) => copy with { Volume = copy.Volume with { FileSystem = fileSystem } };

        var firstKept = On(Found(Write(Path.Combine(Documents, "a.bin"), Content(seed: 1))));
        var first = On(Found(Write(Path.Combine(Downloads, "a.bin"), Content(seed: 1))));
        var secondKept = On(Found(Write(Path.Combine(Documents, "b.bin"), Content(seed: 2))));
        var second = On(Found(Write(Path.Combine(Downloads, "b.bin"), Content(seed: 2))));
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
        Assert.True(File.Exists(aside));

        // The other copy went in the same operation, and is identified in the bin as its own copy.
        Assert.Equal(RemovalCheck.Removed, Assert.Single(report.Copies, copy => copy != stopped).Check);
    }

    /// <summary>
    /// FAT32 and exFAT number a file by where its entry lies, so the item the bin received reads with
    /// another number than the copy compared. The handle the copy was compared through followed it into
    /// the bin, which is what shows it is the copy, so it is removed and the run goes on.
    /// </summary>
    [Fact]
    public void ACopyRenumberedByItsMoveIntoTheBinOnADriveThatKeepsNoNumbersIsRemoved()
    {
        DuplicateCandidate OnFat(DuplicateCandidate copy) => copy with { Volume = copy.Volume with { FileSystem = "FAT32" } };

        var kept = OnFat(Found(Write(Path.Combine(Documents, "a.bin"), Content())));
        var copy = OnFat(Found(Write(Path.Combine(Downloads, "a.bin"), Content())));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var renumbering = new FileInformation(FileInformation.Open, (SafeFileHandle handle, IdentityRoute route, out FileIdentity identity) =>
        {
            var read = FileInformation.ReadIdentity(handle, route, out identity);

            if (FileInformation.FinalPathOf(handle) is { } at && LongPath.Display(at).StartsWith(Bin, StringComparison.OrdinalIgnoreCase))
            {
                identity = identity with { File = identity.File + 1 };
            }

            return read;
        });

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.MovingTo(Bin), files: renumbering);

        Assert.Equal(RemovalCheck.Removed, Assert.Single(report.Copies).Check);
        Assert.Null(report.StoppedAt);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>A bin that moves the copy and does not say where leaves nothing to identify, so the run stops there.</summary>
    [Fact]
    public void AMoveTheBinDoesNotPlaceStopsTheRun()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var marks = Marks([kept, copy]);
        Mark(marks, copy);

        var bin = new FakeRecycleBin(path =>
        {
            Directory.CreateDirectory(Bin);
            File.Move(path, Path.Combine(Bin, "moved.bin"));
            return new RecycleOutcome(Removed: true);
        });

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: bin);

        Assert.Equal(RemovalCheck.BinUnconfirmed, Assert.Single(report.Copies).Check);
        Assert.NotNull(report.StoppedAt);
    }

    /// <summary>
    /// Windows deleting a copy outright when asked to move it to the bin stops the run, and the copy
    /// is said to be gone and not in the bin, never moved there or still in place. §5.6 does not count
    /// it as a loss beside it, since the report names it. The copy beside it in the same operation,
    /// which Windows did move, is reported moved: the outright deletion is that one copy's alone.
    /// </summary>
    [Fact]
    public void ACopyWindowsDeletedOutrightStopsTheRunAndIsNotSaidToBeInTheBin()
    {
        var kept = Found(Write(Path.Combine(Documents, "a.bin"), Content()));
        var copy = Found(Write(Path.Combine(Downloads, "a.bin"), Content()));
        var keptB = Found(Write(Path.Combine(Documents, "b.bin"), Content(seed: 2)));
        var copyB = Found(Write(Path.Combine(Downloads, "b.bin"), Content(seed: 2)));
        var marks = Marks([kept, copy], [keptB, copyB]);
        Mark(marks, copy);
        Mark(marks, copyB);

        var moving = FakeRecycleBin.MovingTo(Bin);
        var bin = new FakeRecycleBin(path =>
        {
            if (path != copy.Path)
            {
                return moving.Recycle([path])[0];
            }

            File.Delete(path);
            return new RecycleOutcome(Removed: true, "Deleted outright.") { DeletedOutright = true };
        });

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: bin);

        var first = report.Copies.Single(outcome => outcome.Check is RemovalCheck.DeletedOutright);
        Assert.Same(first, report.StoppedAt);
        Assert.Equal(copy, first.Copy);
        Assert.Contains("outright", first.Message);
        Assert.Equal(copyB, Assert.Single(report.Removed).Copy);
        Assert.DoesNotContain(first, report.Kept);
        Assert.True(report.Verification.Passed);
        Assert.DoesNotContain("Nothing was removed", report.Summary);
    }

    /// <summary>
    /// A copy that stops the run stops it once its batch has gone. The copies in the batch after it
    /// are not reached: they are where they were, and nothing hands them to the bin.
    /// </summary>
    [Fact]
    public void ACopyThatStopsTheRunLeavesTheNextBatchUnreached()
    {
        List<DuplicateCandidate[]> groups = [];

        for (var i = 0; i <= IRecycleBin.LargestBatch; i++)
        {
            var content = Content(length: 1024, seed: i + 1);
            groups.Add([
                Found(Write(Path.Combine(Documents, $"{i}.bin"), content)),
                Found(Write(Path.Combine(Downloads, $"{i}.bin"), content))]);
        }

        var marks = Marks([.. groups]);
        Mark(marks, [.. groups.Select(group => group[1])]);
        var first = groups[0][1];
        var moving = FakeRecycleBin.MovingTo(Bin);
        var bin = new FakeRecycleBin(path =>
        {
            if (path != first.Path)
            {
                return moving.Recycle([path])[0];
            }

            File.Delete(path);
            return new RecycleOutcome(Removed: true, "Deleted outright.") { DeletedOutright = true };
        });

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: bin);

        Assert.Equal(first, report.StoppedAt?.Copy);
        Assert.Equal([IRecycleBin.LargestBatch], bin.Batches.Select(batch => batch.Count));
        var last = Assert.Single(report.Copies, outcome => outcome.Check == RemovalCheck.NotReached);
        Assert.Equal(groups[^1][1], last.Copy);
        Assert.True(File.Exists(last.Copy.Path));
        Assert.True(report.Verification.Passed);
    }

    /// <summary>
    /// A copy compared and waiting for the rest of its batch is hidden before the batch goes. It is
    /// described again as the batch goes, so it stays and is never handed to the bin, and the copy
    /// beside it in the batch goes.
    /// </summary>
    [Fact]
    public void ACopyThatChangesWhileItWaitsForItsBatchStaysAndTheRestGo()
    {
        var keptA = Found(Write(Path.Combine(Documents, "a.bin"), Content(seed: 1)));
        var copyA = Found(Write(Path.Combine(Downloads, "a.bin"), Content(seed: 1)));
        var keptB = Found(Write(Path.Combine(Documents, "b.bin"), Content(seed: 2)));
        var copyB = Found(Write(Path.Combine(Downloads, "b.bin"), Content(seed: 2)));
        var marks = Marks([keptA, copyA], [keptB, copyB]);
        Mark(marks, copyA, copyB);
        var bin = FakeRecycleBin.MovingTo(Bin);

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: bin, open: (extended, use) =>
        {
            // The second copy is opened once the first has been compared and is waiting.
            if (use is HeldFor.Removing && extended == LongPath.Extended(copyB.Path))
            {
                File.SetAttributes(copyA.Path, File.GetAttributes(copyA.Path) | FileAttributes.Hidden);
            }

            return FileInformation.OpenHeld(extended, use);
        });

        Assert.Equal(RemovalCheck.Changed, report.Copies.Single(outcome => outcome.Copy == copyA).Check);
        Assert.True(File.Exists(copyA.Path));
        Assert.Equal(RemovalCheck.Removed, report.Copies.Single(outcome => outcome.Copy == copyB).Check);
        Assert.Equal([[copyB.Path]], bin.Batches);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>
    /// The copy a group keeps stays held until that group's copies have gone, though the run has
    /// moved on to another group by then: the bin, moving the second group's copy in the same batch,
    /// cannot delete the copy the first group keeps.
    /// </summary>
    [Fact]
    public void TheCopyAGroupKeepsIsHeldUntilItsBatchHasGone()
    {
        var keptA = Found(Write(Path.Combine(Documents, "a.bin"), Content(seed: 1)));
        var copyA = Found(Write(Path.Combine(Downloads, "a.bin"), Content(seed: 1)));
        var keptB = Found(Write(Path.Combine(Documents, "b.bin"), Content(seed: 2)));
        var copyB = Found(Write(Path.Combine(Downloads, "b.bin"), Content(seed: 2)));
        var marks = Marks([keptA, copyA], [keptB, copyB]);
        Mark(marks, copyA, copyB);
        var refused = false;

        var report = Remove(marks, ExploreRemovalMode.RecycleBin, bin: FakeRecycleBin.MovingTo(Bin, path =>
        {
            if (path != copyB.Path)
            {
                return;
            }

            try
            {
                File.Delete(keptA.Path);
            }
            catch (IOException)
            {
                refused = true;
            }
        }));

        Assert.True(refused);
        Assert.True(File.Exists(keptA.Path));
        Assert.All(report.Copies, outcome => Assert.Equal(RemovalCheck.Removed, outcome.Check));
        Assert.True(report.Verification.Passed);
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
        var confirmation = RemovalConfirmation.For(marks, KeepingWithDocumentsCleaned(kept), ExploreRemovalMode.Permanent, _ => null, _ => null);
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
        var confirmation = RemovalConfirmation.For(marks, marks.Keeping, ExploreRemovalMode.Permanent, _ => null, _ => null);

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
