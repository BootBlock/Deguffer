using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A directory that goes whole or not at all is judged on the walk its removal then acts on, so each
/// tree it destroys is listed once. Driven through the executor, with the filesystem it removes
/// through, so §5.6's verification reads every outcome.
/// </summary>
public sealed class WholeDirectoryRemovalTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public enum Whole
    {
        AllOrNothing,
        Indivisible,
        Indexed,
    }

    /// <summary>
    /// Every folder of every tree the step destroys is listed once. A look of its own before the
    /// removal listed each of them twice in a row.
    /// </summary>
    [Theory]
    [InlineData(Whole.AllOrNothing)]
    [InlineData(Whole.Indivisible)]
    [InlineData(Whole.Indexed)]
    public async Task ListsEachFolderOnce(Whole whole)
    {
        var output = _temp.CreateDirectory("project", "out");
        _temp.CreateFile(16, "project", "out", "a", "one.bin");
        _temp.CreateFile(16, "project", "out", "a", "b", "two.bin");
        var index = _temp.CreateDirectory("project", "index");
        _temp.CreateFile(16, "project", "index", "o", "manifest.txt");

        var step = whole switch
        {
            Whole.AllOrNothing => new DeleteDirectoryStep(output, "Output") { IsAllOrNothing = true },
            Whole.Indivisible => new DeleteDirectoryStep(output, "Output") { IsIndivisible = true },
            _ => new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] },
        };

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);
        var result = await Run(step, MinimumAge.Off, recorder);

        Assert.True(Assert.Single(result.Steps).Succeeded);
        Assert.False(Directory.Exists(output));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        var listings = recorder.Listed.GroupBy(LongPath.Display, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.NotEmpty(listings);
        Assert.All(listings, folder => Assert.True(folder.Count() == 1, $"{folder.Key} was listed {folder.Count()} times"));

        // §6.3: the one walk lists by the extended form.
        Assert.All(recorder.Listed, path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));

        var expected = whole is Whole.Indexed ? 5 : 3;
        Assert.Equal(expected, listings.Count);
    }

    /// <summary>
    /// §9 in an index: a store in it stops the step before anything is deleted, so the index, the
    /// directory it indexes and the store all stay (§5.6).
    /// </summary>
    [Fact]
    public async Task AStoreInTheIndexStopsTheStepAndEverythingStays()
    {
        var output = _temp.CreateDirectory("project", "out");
        var built = _temp.CreateFile(1024, "project", "out", "built.bin");
        var index = _temp.CreateDirectory("project", "index");
        var manifest = _temp.CreateFile(64, "project", "index", "manifest.txt");
        var store = _temp.CreateFile(8192, "project", "index", "saved", "archive.pst");

        var result = await Run(new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] }, MinimumAge.Off);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.MailStores);
        Assert.StartsWith($"Nothing was removed: this holds an Outlook data file, at {store}.", outcome.Message!, StringComparison.Ordinal);
        Assert.True(File.Exists(store));
        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(built));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A folder Windows will not list in the indexed directory stops the step before its index goes:
    /// the index removed and the directory then left in part would be a failed build.
    /// </summary>
    [Fact]
    public async Task AFolderThatWillNotBeListedStopsTheStepBeforeItsIndexGoes()
    {
        var output = _temp.CreateDirectory("project", "out");
        var locked = _temp.CreateDirectory("project", "out", "locked");
        var hidden = _temp.CreateFile(16, "project", "out", "locked", "hidden.bin");
        var built = _temp.CreateFile(1024, "project", "out", "built.bin");
        var index = _temp.CreateDirectory("project", "index");
        var manifest = _temp.CreateFile(64, "project", "index", "manifest.txt");

        var result = await Run(
            new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] },
            MinimumAge.Off,
            new UnlistableFileSystem(WindowsFileSystem.Default, locked));

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith(
            $"Nothing was removed: Windows would not let Deguffer look inside {locked},", outcome.Message!, StringComparison.Ordinal);
        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(built));
        Assert.True(File.Exists(hidden));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A file that takes a folder's place inside the tree, after the folder holding it was listed and
    /// before the folder itself was, is named as a file and not as a folder Windows refused. Nothing
    /// goes, and the file stays (§5.6).
    /// </summary>
    [Fact]
    public async Task AFileThatTookThePlaceOfAFolderInsideStopsTheStep()
    {
        var output = _temp.CreateDirectory("cache");
        var old = _temp.CreateFile(1024, "cache", "kept.bin");
        var swapped = _temp.CreateDirectory("cache", "parts");
        _temp.CreateFile(16, "cache", "parts", "one.bin");

        var fs = new ListingHookFileSystem(WindowsFileSystem.Default, swapped, () =>
        {
            Directory.Delete(swapped, recursive: true);
            File.WriteAllBytes(swapped, new byte[32]);
        });

        var result = await Run(new DeleteDirectoryStep(output, "A cache") { IsAllOrNothing = true }, MinimumAge.Off, fs);

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith(
            $"Nothing was removed: a file now stands where the folder {swapped} was,", outcome.Message!, StringComparison.Ordinal);
        Assert.Equal(32, new FileInfo(swapped).Length);
        Assert.True(File.Exists(old));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A file the guard would keep stops a step whose parts only mean something together, and nothing
    /// in it goes. An indivisible step is about Outlook data files alone, so the same file stays and
    /// the rest goes.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARecentFileStopsAnAllOrNothingStepAndNotAnIndivisibleOne(bool allOrNothing)
    {
        var output = _temp.CreateDirectory("cache");
        var old = _temp.CreateFile(1024, "cache", "old", "image.wim");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var recent = _temp.CreateFile(64, "cache", "new", "manifest.xml");

        var step = allOrNothing
            ? new DeleteDirectoryStep(output, "A cache") { IsAllOrNothing = true }
            : new DeleteDirectoryStep(output, "A cache") { IsIndivisible = true };

        var result = await Run(step, MinimumAge.WithinHours(8, DateTime.UtcNow));

        var outcome = Assert.Single(result.Steps);
        Assert.True(File.Exists(recent));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        if (allOrNothing)
        {
            Assert.False(outcome.Succeeded);
            Assert.StartsWith("Nothing was removed: something inside ", outcome.Message!, StringComparison.Ordinal);
            Assert.True(File.Exists(old));
        }
        else
        {
            Assert.True(outcome.Succeeded);
            Assert.Equal(1, outcome.Kept);
            Assert.False(File.Exists(old));
        }
    }

    /// <summary>
    /// A folder inside whose own timestamps the guard protects stops an all-or-nothing step, though
    /// every file in it is old: something was written in it or taken out of it since, and its owner may
    /// be working in it again. The same tree with the folder dated old goes whole.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARecentFolderAloneStopsAnAllOrNothingStep(bool folderIsRecent)
    {
        var month = DateTime.UtcNow.AddDays(-30);
        var output = _temp.CreateDirectory("agent");
        var image = _temp.CreateFile(1024, "agent", "Backup", "winre.wim");
        Date(image);
        var backup = Path.GetDirectoryName(image)!;

        if (!folderIsRecent)
        {
            Directory.SetCreationTimeUtc(backup, month);
            Directory.SetLastWriteTimeUtc(backup, month);
        }

        var result = await Run(
            new DeleteDirectoryStep(output, "Leftovers") { IsAllOrNothing = true }, MinimumAge.WithinHours(8, DateTime.UtcNow));

        var outcome = Assert.Single(result.Steps);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
        Assert.Equal(!folderIsRecent, outcome.Succeeded);
        Assert.Equal(folderIsRecent, File.Exists(image));

        if (folderIsRecent)
        {
            Assert.StartsWith("Nothing was removed: something inside ", outcome.Message!, StringComparison.Ordinal);
        }

        void Date(string file)
        {
            File.SetCreationTimeUtc(file, month);
            File.SetLastWriteTimeUtc(file, month);
            File.SetLastAccessTimeUtc(file, month);
        }
    }

    /// <summary>
    /// An index Windows will not remove in full leaves the directory it indexes untouched, because
    /// what is left of the index still names outputs by where they are (§5.6).
    /// </summary>
    [Fact]
    public async Task AnIndexThatCannotBeRemovedInFullLeavesTheIndexedDirectoryUntouched()
    {
        var output = _temp.CreateDirectory("project", "out");
        var built = _temp.CreateFile(1024, "project", "out", "built.bin");
        var index = _temp.CreateDirectory("project", "index");
        var held = _temp.CreateFile(64, "project", "index", "manifest.txt");

        CleanupResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await Run(new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] }, MinimumAge.Off);
        }

        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.StartsWith($"Left {output} alone: part of its index could not be removed", outcome.Message!, StringComparison.Ordinal);
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(built));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A clean cancelled while the trees are gathered stops before anything is deleted, and says so.
    /// </summary>
    [Fact]
    public async Task ACancelWhileGatheringRemovesNothing()
    {
        var output = _temp.CreateDirectory("project", "out");
        var built = _temp.CreateFile(1024, "project", "out", "nested", "built.bin");
        var index = _temp.CreateDirectory("project", "index");
        var manifest = _temp.CreateFile(64, "project", "index", "manifest.txt");

        using var cts = new CancellationTokenSource();
        var fs = new ListingHookFileSystem(WindowsFileSystem.Default, Path.Combine(output, "nested"), cts.Cancel);

        var outcome = await WholeDirectoryRemoval.RemoveAsync(
            new DeleteDirectoryStep(output, "Output") { IndexedBy = [index] },
            MinimumAge.Off,
            RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
            new RunResidue(),
            progress: null,
            fs,
            TimeProvider.System,
            new ThreadAllowance(DirectoryRemover.MostAtOnce),
            cts.Token);

        Assert.True(outcome.Interrupted);
        Assert.False(outcome.Succeeded);
        Assert.Equal("Stopped when the clean was cancelled, before anything was removed.", outcome.Message);
        Assert.True(File.Exists(built));
        Assert.True(File.Exists(manifest));
    }

    private Task<CleanupResult> Run(DeleteDirectoryStep step, MinimumAge keep, IFileSystem? fs = null) =>
        new PlanExecutor(
                new FakeProcessRunner(),
                ParallelEnumerationScanner.Default,
                RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
                fileSystem: fs)
            .ExecuteAsync(
                new CleanupPlan
                {
                    ProviderId = "test",
                    ProviderName = "Test",
                    Tier = SafetyTier.RegenerableCache,
                    WhatHappensOnNextUse = "Nothing.",
                    Steps = [step],
                    Keep = keep,
                },
                runReach: null,
                residue: null,
                progress: null,
                underway: null,
                default);
}
