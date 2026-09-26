using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the executor reports a run reclaimed, which is the one number the user checks the tool
/// against. §5.4 names the cost of getting it wrong: "the user will prune, see no change, and lose
/// trust in the tool."
///
/// It had no test file of its own. Seven of the eight providers that emit a command step never call
/// <c>ExecuteAsync</c> in their own tests, so the arithmetic below ran unexamined.
/// </summary>
public sealed class PlanExecutorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A record under this test's own invented profile. The executor requires one precisely so that
    /// nothing can default to the signed-in user's, which is what a suite run would otherwise write
    /// into.
    /// </summary>
    private RefusalRecord RefusalLog => RefusalRecord.For(new FakeUserEnvironment(_temp.Path));

    /// <summary>
    /// Every deletion records where Windows refused it, so the next preview of the same location can
    /// leave out what is still refused — and a later clean that is refused nothing clears what an
    /// earlier one recorded, or the row would go on hiding bytes that are free to go.
    /// </summary>
    [Fact]
    public async Task RecordsWhereADeletionWasRefusedAndClearsItOnceNothingIs()
    {
        var cache = _temp.CreateDirectory("cache");
        var held = _temp.CreateFile(2048, "cache", "packages", "held.nupkg");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var plan = PlanDeleting(new DeleteDirectoryStep(cache, "A cache"));

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var refused = await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, default);

            Assert.Equal(new RefusalTally(1, 2048), refused.Refused.InUse);
            Assert.Equal([Path.Combine(cache, "packages")], RefusalLog.At(cache));
        }

        await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, default);

        Assert.Empty(RefusalLog.At(cache));
    }

    /// <summary>
    /// A single named file is its own place, and a file the guard kept was not asked about at all —
    /// so an earlier refusal there is still the latest answer, and must not be cleared by a step that
    /// never reached Windows.
    /// </summary>
    [Fact]
    public async Task RecordsARefusedFileAsItselfAndLeavesTheRecordAloneWhenTheGuardKeptIt()
    {
        var dump = _temp.CreateFile(4096, "Windows", "MEMORY.DMP");
        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var step = new DeleteFileStep(dump, "A crash dump");

        using (new FileStream(dump, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await executor.ExecuteAsync(PlanDeleting(step), runReach: null, residue: null, progress: null, default);
        }

        Assert.Equal([dump], RefusalLog.At(dump));

        var guarded = PlanDeleting(step) with { Keep = MinimumAge.WithinHours(8, DateTime.UtcNow) };
        var kept = await executor.ExecuteAsync(guarded, runReach: null, residue: null, progress: null, default);

        Assert.Equal(1, Assert.Single(kept.Steps).Kept);
        Assert.Equal([dump], RefusalLog.At(dump));
    }

    /// <summary>
    /// A clear that took something and was denied the rest says so in those words. Windows' own
    /// refusal is staged, so the reason is the one a real denial produces rather than a fake's.
    /// </summary>
    [Fact]
    public async Task SaysAClearLeftWhatWindowsDeniedRatherThanCallingItInUse()
    {
        var scratch = _temp.CreateDirectory("scratch");
        _temp.CreateFile(1024, "scratch", "abandoned.tmp");
        var guarded = _temp.CreateFile(2048, "scratch", "profile", "Cookies");

        using var undeletable = new UndeletableFile(guarded);

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(
            PlanDeleting(new ClearDirectoryStep(scratch, "Scratch files")), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Contains(
            $"1 file(s) ({FreeSpace.Format(2048)}) left in place because Windows would not let Deguffer remove them",
            step.Message!,
            StringComparison.Ordinal);
        Assert.DoesNotContain("in use", step.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(guarded), "the fixture let a denied file go");
    }

    /// <summary>
    /// §9 through each of the three removals a plan can hold. The store stays, the step still
    /// succeeds at what it could do, and its sentence names the rule rather than a refusal, the guard
    /// or a program in use — the three things a reader would otherwise go looking for.
    /// </summary>
    [Fact]
    public async Task ADirectoryStepLeavesAnOutlookDataFileAndSaysWhy()
    {
        var cache = _temp.CreateDirectory("cache");
        var archive = _temp.CreateFile(4096, "cache", "saved", "archive.pst");
        _temp.CreateFile(1024, "cache", "blob.bin");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(
            PlanDeleting(new DeleteDirectoryStep(cache, "A cache")), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.True(File.Exists(archive), "an Outlook data file was deleted");
        Assert.True(step.Succeeded);
        Assert.Equal(1, step.MailStores);
        Assert.Equal(1, result.MailStoreCount);
        Assert.Equal(1024, step.BytesReclaimed);
        Assert.Contains("1 Outlook data file(s) left alone", step.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain("changed recently", step.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClearStepLeavesAnOutlookDataFileAndSaysWhy()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var archive = _temp.CreateFile(4096, "scratch", "Temp1_mail.zip", "archive.pst");
        _temp.CreateFile(1024, "scratch", "abandoned.tmp");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(
            PlanDeleting(new ClearDirectoryStep(scratch, "Scratch files")), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.True(File.Exists(archive), "an Outlook data file was deleted");
        Assert.True(step.Succeeded);
        Assert.Equal(1, step.MailStores);
        Assert.StartsWith("Cleared", step.Message!, StringComparison.Ordinal);
        Assert.Contains("1 Outlook data file(s) left alone", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step whose whole subject is a store did nothing, and nothing was meant to go: the same shape
    /// as a file the guard kept, and reported the same way — a success that says what stayed.
    /// </summary>
    [Fact]
    public async Task AFileStepNamingAnOutlookDataFileLeavesItAndSaysWhy()
    {
        var archive = _temp.CreateFile(4096, "Downloads", "archive.pst");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(
            PlanDeleting(new DeleteFileStep(archive, "A file")), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.True(File.Exists(archive), "an Outlook data file was deleted");
        Assert.True(step.Succeeded);
        Assert.Equal(1, step.MailStores);
        Assert.Equal(0, step.EntriesRemoved);
        Assert.Contains("Outlook data file", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan is made minutes before it runs, and a store can arrive in between — an archive saved
    /// into a cache folder while the preview sat on screen. A tool's own command cannot leave it, so
    /// the executor looks on the disk again immediately before it runs one, as it asks the guard
    /// again before it deletes a file.
    /// </summary>
    [Fact]
    public async Task DoesNotRunAToolsCommandWhenAStoreArrivedInsideWhatItClearsAfterThePreview()
    {
        var cache = _temp.CreateDirectory("npm-cache");
        _temp.CreateFile(4096, "npm-cache", "_cacache", "blob");

        var command = new RunCommandStep("npm.cmd", "cache clean --force", "Clear the npm cache")
        {
            Estimated = new ScanSize(4096, 4096),
            MeasuredPaths = [cache],
        };

        var archive = _temp.CreateFile(8192, "npm-cache", "saved", "archive.pst");

        var runner = new FakeProcessRunner();
        var executor = new PlanExecutor(runner, ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(PlanDeleting(command), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.Empty(runner.Invocations);
        Assert.False(step.Succeeded);
        Assert.Equal(1, step.MailStores);
        Assert.Contains(archive, step.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(archive));
    }

    /// <summary>The same for a Recycle Bin Windows would empty whole: a store deleted into it after the preview.</summary>
    [Fact]
    public async Task DoesNotEmptyABinWhenAStoreArrivedInItAfterThePreview()
    {
        var volume = _temp.CreateDirectory("volumes", "D");
        var bin = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier);
        var store = _temp.CreateFile(8192, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RA1B2C3.pst");

        var emptier = new FakeRecycleBinEmptier();
        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog, emptier);
        var result = await executor.ExecuteAsync(
            PlanDeleting(new EmptyRecycleBinStep(bin, "A bin")), runReach: null, residue: null, progress: null, default);

        var step = Assert.Single(result.Steps);

        Assert.Empty(emptier.VolumeRoots);
        Assert.False(step.Succeeded);
        Assert.Equal(1, step.MailStores);
        Assert.True(File.Exists(store));
        Assert.True(Directory.Exists(volume));
    }

    /// <summary>
    /// A Recycle Bin removed file by file goes whole or not at all: a deleted folder and the record
    /// that restores it are two entries. Stepping over a store that arrived in one after the preview
    /// would keep the store and take its record, so an indivisible step is looked at again on the disk
    /// and refused whole.
    /// </summary>
    [Fact]
    public async Task DoesNotRemoveAnIndivisibleFolderWhenAStoreArrivedInItAfterThePreview()
    {
        var bin = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier);
        var step = new DeleteDirectoryStep(bin, "A bin") { IsIndivisible = true };

        var store = _temp.CreateFile(
            8192, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RDEF456", "mail", "archive.pst");
        var record = _temp.CreateFile(544, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$IDEF456");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);
        var result = await executor.ExecuteAsync(PlanDeleting(step), runReach: null, residue: null, progress: null, default);

        var outcome = Assert.Single(result.Steps);

        Assert.True(File.Exists(store), "a store was removed");
        Assert.True(File.Exists(record), "the record that restores the folder holding a store was removed");
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, outcome.MailStores);
        Assert.Contains(store, outcome.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §9 against §5.3 for a tool's own command. The tool clears what it was sent to whole, so a folder
    /// inside that Windows will not let Deguffer list could hold a store nobody saw, and the command
    /// does not run. The look used to skip that folder and answer "no store".
    /// </summary>
    [Fact]
    public async Task DoesNotRunAToolsCommandWhenAFolderInsideWhatItClearsWillNotBeListed()
    {
        var cache = _temp.CreateDirectory("npm-cache");
        _temp.CreateFile(4096, "npm-cache", "_cacache", "blob");
        var locked = _temp.CreateDirectory("npm-cache", "saved");
        var archive = _temp.CreateFile(8192, "npm-cache", "saved", "archive.pst");

        var command = new RunCommandStep("npm.cmd", "cache clean --force", "Clear the npm cache")
        {
            Estimated = new ScanSize(4096, 4096),
            MeasuredPaths = [cache],
        };

        var runner = new FakeProcessRunner();
        var executor = new PlanExecutor(runner, ParallelEnumerationScanner.Default, RefusalLog);

        StepOutcome step;
        using (new DeniedDirectory(locked))
        {
            step = Assert.Single(
                (await executor.ExecuteAsync(PlanDeleting(command), runReach: null, residue: null, progress: null, default)).Steps);
        }

        Assert.Empty(runner.Invocations);
        Assert.False(step.Succeeded);
        Assert.Equal(0, step.MailStores);
        Assert.StartsWith($"Not run: Windows would not let Deguffer look inside {locked},", step.Message!, StringComparison.Ordinal);
        Assert.True(File.Exists(archive));
    }

    /// <summary>The same for a Recycle Bin Windows would empty whole: a deleted folder it will not list.</summary>
    [Fact]
    public async Task DoesNotEmptyABinWhenAFolderInItWillNotBeListed()
    {
        var volume = _temp.CreateDirectory("volumes", "D");
        var bin = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier);
        var locked = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RDEF456");
        var store = _temp.CreateFile(
            8192, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RDEF456", "archive.pst");

        var emptier = new FakeRecycleBinEmptier();
        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog, emptier);

        StepOutcome step;
        using (new DeniedDirectory(locked))
        {
            step = Assert.Single((await executor.ExecuteAsync(
                PlanDeleting(new EmptyRecycleBinStep(bin, "A bin")), runReach: null, residue: null, progress: null, default)).Steps);
        }

        Assert.Empty(emptier.VolumeRoots);
        Assert.False(step.Succeeded);
        Assert.Contains(locked, step.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store));
        Assert.True(Directory.Exists(volume));
    }

    /// <summary>
    /// The same for a Recycle Bin removed file by file. The walk would leave the folder it cannot list
    /// and take the record that restores it, so a store inside would stay with nothing able to put it
    /// back. The step is refused whole.
    /// </summary>
    [Fact]
    public async Task DoesNotRemoveAnIndivisibleFolderWhenAFolderInItWillNotBeListed()
    {
        var bin = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier);
        var locked = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RDEF456");
        var store = _temp.CreateFile(
            8192, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RDEF456", "archive.pst");
        var record = _temp.CreateFile(544, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$IDEF456");

        var executor = new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog);

        StepOutcome outcome;
        using (new DeniedDirectory(locked))
        {
            outcome = Assert.Single((await executor.ExecuteAsync(
                PlanDeleting(new DeleteDirectoryStep(bin, "A bin") { IsIndivisible = true }),
                runReach: null, residue: null, progress: null, default)).Steps);
        }

        Assert.True(File.Exists(store), "a store was removed");
        Assert.True(File.Exists(record), "the record that restores a folder nobody could list was removed");
        Assert.False(outcome.Succeeded);
        Assert.Contains(locked, outcome.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The last check before a removal. A provider should never plan the account's own folder, and
    /// every provider handed a folder by a setting asks first — but a step that names one anyway must
    /// not run, because it would take somebody's files and §5.6 would protect only the profile above.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NeverRemovesOneOfTheAccountsOwnFoldersEvenWhenAStepNamesIt(bool clear, bool extended)
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var plain = Path.Combine(environment.UserProfile, "Downloads");
        var downloads = extended ? LongPath.Extended(plain) : plain;
        Assert.Equal(extended, downloads.StartsWith(@"\\?\", StringComparison.Ordinal));
        var kept = _temp.CreateFile(2048, "profile", "Downloads", "setup.exe");
        var step = clear
            ? (CleanupStep)new ClearDirectoryStep(downloads, "A cache")
            : new DeleteDirectoryStep(downloads, "A cache");

        var result = await new PlanExecutor(
                new FakeProcessRunner(),
                ParallelEnumerationScanner.Default,
                RefusalLog,
                environment: environment,
                system: new FakeSystemDirectories(Path.Combine(_temp.Path, "machine")))
            .ExecuteAsync(PlanDeleting(step), runReach: null, residue: null, progress: null, default);

        Assert.True(File.Exists(kept));
        var outcome = Assert.Single(result.Steps);
        Assert.False(outcome.Succeeded);
        Assert.Contains("one of your own folders", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.BytesReclaimed);
    }

    private static CleanupPlan PlanDeleting(CleanupStep step) => new()
    {
        ProviderId = "test",
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = [step],
    };

    /// <summary>
    /// A command step's reclaim is the plan-time figure minus a re-measurement of the same paths,
    /// and both readings go through the provider's own scanner. Where the volume index serves them
    /// the second one is not a measurement at all: it is the same pre-command snapshot, because
    /// nothing invalidates the index between planning and executing — <c>Invalidate</c> is called
    /// once, at the top of a planning pass. The two readings cancel and a clean that freed
    /// gigabytes reports nothing.
    ///
    /// <para>Observed on a real volume, elevated, before this test was written: a 10 MB tree
    /// measured through the index at 10,485,760 bytes, deleted, then measured again through the
    /// same scanner at 10,485,760 bytes. Not a rounding difference — the identical figure.</para>
    ///
    /// <para>The fixture serves the table from before the command ran, which is exactly the
    /// snapshot the product holds: the index is built during planning and kept for the life of the
    /// pass. The command here deletes the tree, so a genuine second look answers zero.</para>
    /// </summary>
    [Fact]
    public async Task ReportsWhatACommandFreedRatherThanSubtractingASnapshotFromItself()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096), new TreeFile("b.bin", 8192)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        var planTime = await scanner.MeasureAsync(cache);
        Assert.Equal(ScanStrategy.MasterFileTable, planTime.Strategy);

        // The step's own command is what empties the tree, as a real cache eviction does.
        var runner = new FakeProcessRunner().Replying(_ =>
        {
            Directory.Delete(cache, recursive: true);
            return new CommandOutcome(0, "cleared", string.Empty);
        });

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "Clear the cache with the tool's own command")
                {
                    Estimated = planTime.Size,
                    MeasuredPaths = [cache],
                },
            ],
        };

        var result = await new PlanExecutor(runner, scanner, RefusalLog).ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: default);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(cache), "the fixture command did not actually empty the tree.");
        Assert.Equal(4096 + 8192, result.BytesReclaimed);
    }

    /// <summary>
    /// A command that freed nothing must report nothing, and the trap is that the two readings come
    /// from different routes.
    ///
    /// <para>The plan-time figure can come from the file table, which knows what a file occupies.
    /// The after-measure comes from a walk, which knows only what a file's length is and says so by
    /// setting allocated equal to logical. Subtracting one from the other compares two different
    /// kinds of byte, and the gap is not academic: cluster slack across a cache of small files makes
    /// allocated much the larger, so a clean that removed nothing would report a reclaim.</para>
    ///
    /// <para>The fixture is a file the table says occupies 8192 bytes and whose length is 4096 — the
    /// ordinary shape of a small file on a 4 KB-cluster volume. Every other mirrored tree in the
    /// suite sets the two equal, which is why nothing here could discriminate before.</para>
    /// </summary>
    [Fact]
    public async Task ReportsNothingWhenTheCommandFreedNothing()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096, Allocated: 8192)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        var planTime = await scanner.MeasureAsync(cache);
        Assert.Equal(ScanStrategy.MasterFileTable, planTime.Strategy);
        Assert.Equal(8192, planTime.Size.Allocated);
        Assert.Equal(4096, planTime.Size.Logical);

        // A command that succeeds and clears nothing, which is what a failed eviction looks like
        // from here, and what conda's clean looks like for everything an environment still links.
        var runner = new FakeProcessRunner();

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "Clear the cache with the tool's own command")
                {
                    Estimated = planTime.Size,
                    MeasuredPaths = [cache],
                },
            ],
        };

        var result = await new PlanExecutor(runner, scanner, RefusalLog).ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: default);

        Assert.Equal(0, result.BytesReclaimed);
    }

    /// <summary>
    /// The after-measure must not be served from the index, and that is a property of the executor
    /// rather than of any one provider — so it is asserted where every command step passes.
    /// </summary>
    [Fact]
    public async Task TakesTheAfterMeasureFromDiskRatherThanFromACachedIndex()
    {
        var (cache, fixture) = MirroredTree.Realise(
            _temp,
            new TreeDirectory("cache", new TreeFile("a.bin", 4096)));

        var scanner = new DirectoryScanner(FakeMftSourceFactory.Serving(VolumeLetter(cache), fixture));

        Assert.Equal(ScanStrategy.MasterFileTable, (await scanner.MeasureAsync(cache)).Strategy);

        Directory.Delete(cache, recursive: true);

        // The ordinary route still answers from the snapshot, which is correct for planning and is
        // why the executor cannot use it here.
        Assert.Equal(4096, (await scanner.MeasureAsync(cache)).Size.Reclaimable);
        Assert.Equal(0, (await scanner.MeasureFromDiskAsync(cache)).Size.Reclaimable);
    }

    /// <summary>
    /// A step reports 0 to 1 about itself, and what reaches the caller is that step's slice of the
    /// plan. Getting the offset wrong is not a cosmetic fault: the bar would reach the end while
    /// the first of five steps was still running, and then sit there for the rest of the clean.
    ///
    /// <para>Both steps here finish in one report, so the discriminating value is the first one.
    /// The directory removal's own "done" is 1.0, and it must arrive as 0.5. Neither step carries
    /// an estimate, so this is the equal-share branch of <c>ProgressWeights</c>; the test below
    /// covers the weighting.</para>
    /// </summary>
    [Fact]
    public async Task AStepsOwnFractionArrivesAsItsSliceOfThePlan()
    {
        var directory = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "a.bin");
        _temp.CreateFile(64, "cache", "b.bin");
        var file = _temp.CreateFile(64, "dump.dmp");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new DeleteDirectoryStep(directory, "A cache"),
                new DeleteFileStep(file, "A dump"),
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner(), RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress, default);

        // Repeats are ordinary — the removal reports its last file and then its own completion —
        // so the claim is about which values appear and in what order, not how many times.
        Assert.Equal([0.5, 1.0], progress.Reports.Distinct());
    }

    /// <summary>
    /// Steps share the bar by what each will free, the same rule the planner applies to whole
    /// plans. One <c>obj</c> of 4 GB beside five of 20 MB is six steps, and an equal split would
    /// crawl through the first sixth of the bar and then jump the rest of it.
    /// </summary>
    [Fact]
    public async Task WeightsTheBarByWhatEachStepFreesRatherThanByHowManyThereAre()
    {
        var big = _temp.CreateDirectory("big");
        _temp.CreateFile(64, "big", "a.bin");
        var small = _temp.CreateDirectory("small");
        _temp.CreateFile(64, "small", "a.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new DeleteDirectoryStep(big, "The big one") { Estimated = new ScanSize(9_000, 9_000) },
                new DeleteDirectoryStep(small, "The small one") { Estimated = new ScanSize(1_000, 1_000) },
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner(), RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress, default);

        Assert.Equal([0.9, 1.0], progress.Reports.Select(r => Math.Round(r, 6)).Distinct());
    }

    /// <summary>
    /// A command step reports nothing at all while it runs, so the executor's own report at the end
    /// of each step is the only thing that carries the bar across it.
    ///
    /// <para>The other progress tests here use removals, and a removal reports its own 1.0 on the
    /// way out — which lands on exactly the value the executor would report anyway, and so hides
    /// whether that line ran at all. This is the step type that cannot hide it.</para>
    /// </summary>
    [Fact]
    public async Task ACommandStepAdvancesTheBarThoughItReportsNothingItself()
    {
        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps =
            [
                new RunCommandStep("tool", "clean", "The big one")
                {
                    Estimated = new ScanSize(9_000, 9_000),
                },
                new RunCommandStep("tool", "clean", "The small one")
                {
                    Estimated = new ScanSize(1_000, 1_000),
                },
            ],
        };

        var progress = new ProgressRecorder<double>();

        await new PlanExecutor(new FakeProcessRunner(), new FakeDirectoryScanner(), RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress, default);

        // Two steps, two reports, and nothing else could have produced either of them.
        Assert.Equal([0.9, 1.0], progress.Reports.Select(r => Math.Round(r, 6)));
    }

    /// <summary>
    /// Every reason must have a sentence or a considered silence, because <c>ScanResult</c> exposes
    /// the lookup as a property and a switch with no arm throws rather than returning nothing. A new
    /// member added without one is a crash on a property access, which is a poor way to find out.
    /// </summary>
    [Fact]
    public void EveryFallbackReasonHasAnAnswerRatherThanAThrow()
    {
        foreach (var reason in Enum.GetValues<FallbackReason>())
        {
            var exception = Record.Exception(() => FallbackReasonText.Describe(reason));

            Assert.True(exception is null, $"{reason} has no arm in FallbackReasonText.Describe.");
        }
    }

    private static char VolumeLetter(string path) => char.ToUpperInvariant(path[0]);

    /// <summary>
    /// A directory whose files are all inside the guard window reclaims nothing and keeps its root,
    /// which is the shape the executor otherwise reads as a step that achieved nothing. It is the
    /// setting working, so it reports success and says what it left — a red row for correct
    /// behaviour would teach the user to distrust the report.
    /// </summary>
    [Fact]
    public async Task ReportsAStepThatKeptEverythingAsSuccessRatherThanFailure()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Keep = MinimumAge.WithinHours(8, DateTime.UtcNow),
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: CancellationToken.None);

        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(1, step.Kept);
        Assert.Equal(0, step.BytesReclaimed);
        Assert.Equal(1, result.KeptCount);
        Assert.Contains("changed too recently", step.Message!, StringComparison.Ordinal);
        Assert.True(Directory.Exists(cache), "the guard kept a file and the folder around it went");
    }

    /// <summary>
    /// The guard travels on the plan, so a plan made without one deletes exactly what it always did.
    /// Reading the setting again at execution is what this rules out: the cut-off would then have
    /// moved, and the clean would take files the preview promised to leave.
    /// </summary>
    [Fact]
    public async Task DeletesEverythingWhenThePlanCarriesNoGuard()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: CancellationToken.None);

        Assert.Equal(0, result.KeptCount);
        Assert.Equal(4096, result.BytesReclaimed);
        Assert.False(Directory.Exists(cache));
    }

    /// <summary>
    /// A directory the guard emptied of candidates is still standing, so the step must not say it
    /// was removed. The success classification is right — Deguffer did what it was asked — but
    /// "Removed" is a claim about the user's disk, and here it is false.
    /// </summary>
    [Fact]
    public async Task DoesNotSayRemovedAboutADirectoryThatIsStillThere()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");

        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Keep = MinimumAge.WithinHours(8, DateTime.UtcNow),
            Steps = [new DeleteDirectoryStep(cache, "A cache")],
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: CancellationToken.None);

        var step = Assert.Single(result.Steps);

        Assert.True(Directory.Exists(cache));
        Assert.DoesNotContain("Removed", step.Message!, StringComparison.Ordinal);
        Assert.Contains("changed too recently", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A clearing step empties its folder and leaves it, and the entries it was told to spare stay
    /// with everything under them.
    /// </summary>
    [Fact]
    public async Task ClearsAFolderInPlaceAndLeavesWhatItWasToldToSpare()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live-session");

        _temp.CreateFile(4096, "scratch", "abandoned.tmp");
        _temp.CreateFile(8192, "scratch", "live-session", "working.txt");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files") { Spared = [live] });
        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(4096, result.BytesReclaimed);
        Assert.True(Directory.Exists(scratch), "the folder itself was removed");
        Assert.True(File.Exists(Path.Combine(live, "working.txt")), "a spared entry was emptied");
        Assert.Contains("Cleared", step.Message!, StringComparison.Ordinal);
        Assert.Contains("something is using them", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder whose every entry was spared reclaims nothing and has failed at nothing. "Cleared"
    /// would be a false statement about a folder that is exactly as full as it was, so the message
    /// is about what stayed.
    /// </summary>
    [Fact]
    public async Task DoesNotSayClearedAboutAFolderThatIsStillFull()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live-session");
        _temp.CreateFile(8192, "scratch", "live-session", "working.txt");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files") { Spared = [live] });
        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.DoesNotContain("Cleared", step.Message!, StringComparison.Ordinal);
        Assert.Contains("Nothing was cleared", step.Message!, StringComparison.Ordinal);
        Assert.Contains("something is using them", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case issue #118 names, end to end through a real removal. A clear that fails to spare a
    /// folder a program is working in takes every file in it and leaves that folder and the chain above
    /// it. The run holds no tool's command, so a question about content reads the chain as a survivor.
    /// What the removal left standing inside the protected folder is read instead.
    /// </summary>
    [Fact]
    public async Task AClearThatWentIntoAFolderItShouldHaveSparedFailsVerification()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        var working = _temp.CreateDirectory("scratch", "live", "session", "work");
        _temp.CreateFile(4096, "scratch", "live", "session", "work", "state.bin");
        _temp.CreateFile(1024, "scratch", "abandoned.tmp");

        var plan = PlanDeleting(new ClearDirectoryStep(scratch, "Scratch files")) with
        {
            // Protected as the provider protects a live entry, and left out of Spared: that omission
            // is the over-reach under test.
            ProtectedPaths =
            [
                new ProtectedPath(live, "A program is working in it.", PresenceBefore: PathPresence.Present, HeldContentBefore: true),
            ],
        };

        CleanupResult result;

        using (new HeldDirectory(working))
        {
            result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
                .ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: default);
        }

        Assert.False(File.Exists(Path.Combine(working, "state.bin")), "the removal never went inside the folder");
        Assert.True(Directory.Exists(live), "the fixture let the folder a program was working in go");

        var check = Assert.Single(result.Verification!.Checks);

        Assert.Equal(VerificationOutcome.Entered, check.Outcome);
        Assert.False(result.Verification.Passed);
    }

    /// <summary>
    /// A clear that left a folder a program is working in says so, rather than "Cleared." about a
    /// folder still holding it. The reader answers it by closing that program, which is why the reason
    /// is named.
    /// </summary>
    [Fact]
    public async Task SaysAClearLeftAFolderAnotherProgramWasUsing()
    {
        var scratch = _temp.CreateDirectory("scratch");
        var working = _temp.CreateDirectory("scratch", "tool", "work");
        _temp.CreateFile(1024, "scratch", "abandoned.tmp");

        CleanupResult result;

        using (new HeldDirectory(working))
        {
            result = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        }

        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(new FolderRefusals(InUse: 1, Denied: 0), step.RefusedFolders);
        Assert.Equal(new FolderRefusals(InUse: 1, Denied: 0), result.RefusedFolders);
        Assert.Equal(
            "Cleared, 1 folder(s) left in place because another program was using them.",
            step.Message);
    }

    /// <summary>
    /// A folder that is the whole of a step, and that a program is working in, is not removed and nothing
    /// else happened. The sentence says why rather than "Nothing was removed." with no reason at all, and
    /// a clear whose only outcome was such a folder is not reported as a folder that held nothing.
    /// </summary>
    [Fact]
    public async Task SaysWhyAFolderAProgramIsWorkingInWasNotRemoved()
    {
        var cache = _temp.CreateDirectory("cache");
        var scratch = _temp.CreateDirectory("scratch");
        var working = _temp.CreateDirectory("scratch", "work");

        CleanupResult deleted;
        CleanupResult cleared;

        using (new HeldDirectory(cache))
        using (new HeldDirectory(working))
        {
            deleted = await Execute(new DeleteDirectoryStep(cache, "A cache"));
            cleared = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        }

        var delete = Assert.Single(deleted.Steps);
        var clear = Assert.Single(cleared.Steps);

        Assert.False(delete.Succeeded);
        Assert.Equal("Nothing was removed: another program was using 1 folder(s).", delete.Message);
        Assert.True(Directory.Exists(cache));

        Assert.False(clear.Succeeded);
        Assert.Equal("Nothing was removed: another program was using 1 folder(s).", clear.Message);
    }

    /// <summary>An empty folder is cleared, and says so rather than claiming a reclaim.</summary>
    [Fact]
    public async Task SaysAnEmptyFolderHeldNothingRatherThanFailing()
    {
        var result = await Execute(new ClearDirectoryStep(_temp.CreateDirectory("scratch"), "Scratch files"));
        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Contains("held nothing", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count a run of empty leftovers reports in place of bytes, from each kind of removal Deguffer
    /// carries out itself.
    /// </summary>
    [Fact]
    public async Task ReportsHowManyEntriesEachRemovalTook()
    {
        var folder = _temp.CreateDirectory("leftover");
        _temp.CreateDirectory("leftover", "empty");
        var file = _temp.CreateFile(64, "handshake.lock");

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(
                PlanDeleting(new DeleteDirectoryStep(folder, "A leftover")) with
                {
                    Steps = [new DeleteDirectoryStep(folder, "A leftover"), new DeleteFileStep(file, "A lock")],
                },
                runReach: null,
                residue: null,
                progress: null,
                ct: CancellationToken.None);

        Assert.Equal(2, result.Steps[0].EntriesRemoved);
        Assert.Equal(1, result.Steps[1].EntriesRemoved);
        Assert.Equal(3, result.EntriesRemoved);
        Assert.Equal(64, result.BytesReclaimed);
    }

    /// <summary>
    /// A file that went between the preview and the clean is gone, which is a success, and this clean
    /// took nothing, so it adds no item to what the result says was removed. A folder already gone
    /// counts none for the same reason.
    /// </summary>
    [Fact]
    public async Task CountsNoEntryForAFileThatWasAlreadyGone()
    {
        var result = await Execute(new DeleteFileStep(Path.Combine(_temp.Path, "handshake.lock"), "A lock"));
        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(0, step.EntriesRemoved);
        Assert.Equal(0, result.EntriesRemoved);
    }

    /// <summary>
    /// A folder whose only contents were empty folders did have something cleared out of it. "It held
    /// nothing to clear" would be a false sentence about a folder the user can see was emptied.
    /// </summary>
    [Fact]
    public async Task DoesNotSayAFolderOfEmptyFoldersHeldNothing()
    {
        var scratch = _temp.CreateDirectory("scratch");
        _temp.CreateDirectory("scratch", "empty-one");
        _temp.CreateDirectory("scratch", "empty-two");

        var result = await Execute(new ClearDirectoryStep(scratch, "Scratch files"));
        var step = Assert.Single(result.Steps);

        Assert.True(step.Succeeded);
        Assert.Equal(2, step.EntriesRemoved);
        Assert.True(Directory.Exists(scratch), "the folder cleared in place was removed");
        Assert.DoesNotContain("held nothing", step.Message!, StringComparison.Ordinal);
        Assert.Contains("Cleared", step.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A folder the guard kept standing, with only empty folders taken out of it, lost something. Saying
    /// it was left alone would deny a removal the user can see happened.
    /// </summary>
    [Fact]
    public async Task DoesNotSayAFolderWasLeftAloneWhenEmptyFoldersWentFromIt()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "written-just-now.bin");
        _temp.CreateDirectory("cache", "empty");

        var plan = PlanDeleting(new DeleteDirectoryStep(cache, "A cache")) with
        {
            Keep = MinimumAge.WithinHours(8, DateTime.UtcNow),
        };

        var result = await new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog)
            .ExecuteAsync(plan, runReach: null, residue: null, progress: null, ct: CancellationToken.None);

        var step = Assert.Single(result.Steps);

        Assert.Equal(1, step.EntriesRemoved);
        Assert.False(Directory.Exists(Path.Combine(cache, "empty")));
        Assert.DoesNotContain("Left alone", step.Message!, StringComparison.Ordinal);
    }

    private Task<CleanupResult> Execute(CleanupStep step) =>
        new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, RefusalLog).ExecuteAsync(
            new CleanupPlan
            {
                ProviderId = "test",
                ProviderName = "Test",
                Tier = SafetyTier.RegenerableCache,
                WhatHappensOnNextUse = "Nothing.",
                Steps = [step],
            },
            runReach: null,
            residue: null,
            progress: null,
            ct: CancellationToken.None);
}
