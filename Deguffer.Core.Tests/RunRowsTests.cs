using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The line each location gets under a finished clean's verdict (issue #858). A total, or one paragraph
/// for the whole run, cannot tell a user who cleaned eight rows whether the large one worked.
/// </summary>
public sealed class RunRowsTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Megabyte = 1024L * 1024;

    private static CleanupResult Result(string name, params StepOutcome[] steps) => new()
    {
        ProviderId = name.ToLowerInvariant(),
        ProviderName = name,
        Steps = steps,
    };

    private static StepOutcome Removed(long bytes, Refusals refused = default) =>
        new("Remove the cache", Succeeded: true, bytes, refused);

    private static Refusals InUse(int files, long bytes) => new(new RefusalTally(files, bytes), default);

    private static Refusals Denied(int files, long bytes) => new(default, new RefusalTally(files, bytes));

    /// <summary>
    /// One line per row, each with its own removed bytes and its own refusals. A builder that merged
    /// two rows, or totalled their refusals, would put one row's refusal on another's line.
    /// </summary>
    [Fact]
    public void ThreeResultsYieldALinePerRowWithItsOwnFiguresAndRefusals()
    {
        var rows = RunRows.For(
        [
            Result("Chrome", Removed(Gigabyte, InUse(12, 240 * Megabyte))),
            Result("NuGet", Removed(500 * Megabyte, Denied(3, 10 * Megabyte))),
            Result("pip", Removed(4 * Megabyte)),
        ]).Rows;

        Assert.Equal(["Chrome", "NuGet", "pip"], rows.Select(row => row.Name));

        Assert.Equal($"{FreeSpace.Format(Gigabyte)} removed", rows[0].Removed);
        Assert.Equal(
            [$"12 files ({FreeSpace.Format(240 * Megabyte)}) left in place because another program had them open."],
            rows[0].LeftBehind);

        Assert.Equal($"{FreeSpace.Format(500 * Megabyte)} removed", rows[1].Removed);
        Assert.Equal(
            [$"3 files ({FreeSpace.Format(10 * Megabyte)}) left in place because Windows would not let Deguffer remove them."],
            rows[1].LeftBehind);

        Assert.Equal($"{FreeSpace.Format(4 * Megabyte)} removed", rows[2].Removed);
        Assert.Empty(rows[2].LeftBehind);
    }

    /// <summary>The whole line reads as one passage, name first, for a screen reader.</summary>
    [Fact]
    public void TheLineStartsWithTheNameAndCarriesEverythingTheRowSays()
    {
        var row = RunRow.Of(Result("Chrome", Removed(Gigabyte, InUse(1, 240 * Megabyte))));

        Assert.Equal(
            $"Chrome: {FreeSpace.Format(Gigabyte)} removed. "
            + $"1 file ({FreeSpace.Format(240 * Megabyte)}) left in place because another program had it open.",
            row.Line);
        Assert.Equal($"pip: {FreeSpace.Format(1024)} removed.", RunRow.Of(Result("pip", Removed(1024))).Line);
    }

    /// <summary>
    /// The defect in issue #117: 5.9 GB Windows would not let go was reported as a count that read as
    /// a handful of locked files. A denial carries its size and is never worded as something in use.
    /// </summary>
    [Fact]
    public void ADenialStatesItsSizeAndIsNeverCalledInUse()
    {
        const long guarded = 6_340_000_000;

        var row = RunRow.Of(Result("Temporary files", Removed(0, Denied(252, guarded))));

        var said = Assert.Single(row.LeftBehind);
        Assert.Equal($"252 files ({FreeSpace.Format(guarded)}) left in place because Windows would not let Deguffer remove them.", said);
        Assert.DoesNotContain("another program", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other cause is stated apart, in the order the step messages use, and in the singular for
    /// one: "it", never "them", about a single folder.
    /// </summary>
    [Fact]
    public void StatesEachOtherCauseApartAndAgreesWithOne()
    {
        var step = new StepOutcome(
            "Clear the folder",
            Succeeded: true,
            1024,
            Refusals.None,
            Kept: 1,
            Spared: 1,
            RefusedFolders: new FolderRefusals(InUse: 1, Denied: 2),
            MailStores: 1,
            UserWork: 1,
            Unlisted: 1);

        Assert.Equal(
            [
                "1 folder left in place because another program was using it.",
                "2 folders left in place because Windows would not let Deguffer remove them.",
                "1 folder left in place because Windows would not let Deguffer look inside it.",
                "1 file left alone because it changed recently.",
                "1 item left alone because something is using it.",
                "1 item left alone because it holds work that was never saved anywhere else.",
                "1 Outlook data file left alone, because Deguffer never removes one.",
            ],
            RunRow.Of(Result("Temporary files", step)).LeftBehind);
    }

    /// <summary>
    /// What a step measured staying but could not count, as the Recycle Bin does after Windows empties
    /// it, is stated with its size on the row and counts as something the row left behind.
    /// </summary>
    [Fact]
    public void StatesWhatWasMeasuredStayingButNotCounted()
    {
        var rows = RunRows.For([Result("Recycle Bin", Removed(Gigabyte) with { Unreleased = 3 * Megabyte })]);

        Assert.Equal(
            [$"{FreeSpace.Format(3 * Megabyte)} left in place because Windows would not release it."],
            rows.Rows[0].LeftBehind);
        Assert.Equal("What the location removed, and what it left behind", rows.Heading);
    }

    /// <summary>
    /// What a location removed is stated whatever form it took: nothing at all, entries where no bytes
    /// went, and what a sync app was asked to release or a program will remove at its next start, kept
    /// apart from what went (§5.4).
    /// </summary>
    [Fact]
    public void StatesWhatWentInWhicheverFormItTook()
    {
        Assert.Equal("Nothing removed", RunRow.Of(Result("npm", Removed(0))).Removed);
        Assert.Equal(
            "3 items removed",
            RunRow.Of(Result("Leftovers", Removed(0) with { EntriesRemoved = 3 })).Removed);
        Assert.Equal(
            $"{FreeSpace.Format(2 * Gigabyte)} asked to release",
            RunRow.Of(Result("OneDrive", Removed(0) with { BytesRequested = 2 * Gigabyte })).Removed);
        Assert.Equal(
            $"{FreeSpace.Format(Megabyte)} removed, {FreeSpace.Format(300 * Megabyte)} to be removed when its program next starts",
            RunRow.Of(Result("Tool", Removed(Megabyte) with { BytesScheduled = 300 * Megabyte })).Removed);
    }

    /// <summary>A location the cancel cut short says so first, because every figure on its line is smaller for that reason.</summary>
    [Fact]
    public void ALocationTheCancelCutShortSaysSo()
    {
        var row = RunRow.Of(Result("npm", Removed(1024)) with { Interrupted = true });

        Assert.Equal("The clean was cancelled before this location finished.", row.LeftBehind[0]);
    }

    /// <summary>
    /// A step that failed with nothing counted says why in its own words, such as a command not run
    /// because its program closed. A step that counted what Windows refused is described by the counts,
    /// because its own sentence says the same thing and would be said twice.
    /// </summary>
    [Fact]
    public void AFailedStepSaysWhyOnceInTheWordsThatFitIt()
    {
        const string notRun = "Not run: Code is no longer running, and this command would start it. Open Code and clean again.";
        var command = new StepOutcome("Run the tool", Succeeded: false, 0, Refusals.None, notRun, MailStores: 1);
        var refused = new StepOutcome("Remove the cache", Succeeded: false, 0, Denied(1, 10), "Nothing was removed: Windows would not let Deguffer remove 1 file (10 B).");

        var row = RunRow.Of(Result("Code", command, refused));

        Assert.Equal(
            [$"1 file ({FreeSpace.Format(10)}) left in place because Windows would not let Deguffer remove it.", notRun],
            row.LeftBehind);
    }

    /// <summary>
    /// What the next scan does with a refusal is said once, under the list, and only where something
    /// was refused. A folder holds no bytes the next scan could leave out, so a refused folder alone
    /// does not raise it.
    /// </summary>
    [Fact]
    public void SaysWhatTheNextScanDoesOnceAndOnlyAboutRefusedFiles()
    {
        var refused = RunRows.For([Result("npm", Removed(1, InUse(1, 10))), Result("NuGet", Removed(1, Denied(2, 20)))]);
        var foldersOnly = RunRows.For(
            [Result("Temporary files", new StepOutcome("Clear", true, 1, Refusals.None, RefusedFolders: new FolderRefusals(1, 0)))]);

        Assert.Equal("The next scan leaves out whatever is still refused, apart from a running program's own files.", refused.Note);
        Assert.All(refused.Rows, row => Assert.DoesNotContain(row.LeftBehind, said => said.Contains("next scan", StringComparison.Ordinal)));
        Assert.Empty(foldersOnly.Note);
        Assert.Empty(RunRows.For([Result("pip", Removed(1))]).Note);
    }

    /// <summary>
    /// The heading names how many locations the list covers and how many left something, so a row that
    /// fell short is said while the list is folded away.
    /// </summary>
    [Fact]
    public void TheHeadingSaysHowManyLocationsLeftSomething()
    {
        Assert.Equal("What the location removed", RunRows.For([Result("pip", Removed(1))]).Heading);
        Assert.Equal(
            "What the location removed, and what it left behind",
            RunRows.For([Result("pip", Removed(1, InUse(1, 1)))]).Heading);
        Assert.Equal(
            "What each of the 3 locations removed",
            RunRows.For([Result("a", Removed(1)), Result("b", Removed(1)), Result("c", Removed(1))]).Heading);
        Assert.Equal(
            "What each of the 3 locations removed, and what 2 of them left behind",
            RunRows.For([Result("a", Removed(1, InUse(1, 1))), Result("b", Removed(1)), Result("c", Removed(1) with { Kept = 2 })]).Heading);
    }
}
