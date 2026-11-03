using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How long each step of a clean took, and where a removal spent it. A slow clean reported from a
/// user's machine has to name the step and the pass that were slow: a use check, a look at a whole
/// tree, one large tree, or many small ones.
/// </summary>
public sealed class StepTimingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The use check is timed apart from the rest, and the step's time covers it. The clock moves only
    /// inside the check, so every figure is exact.
    /// </summary>
    [Fact]
    public async Task AStepsTimeCoversItsUseCheck()
    {
        var clock = new ManualTimeProvider();
        var obj = _temp.CreateDirectory("App", "obj");
        _temp.CreateFile(4096, "App", "obj", "project.assets.json");
        var sibling = _temp.CreateFile(10, "App", "App.csproj");

        var check = new AdvancingUseCheck(clock, TimeSpan.FromSeconds(3));
        var result = await Execute(new DeleteDirectoryStep(obj, "Build output") { UseCheck = check }, clock);
        var outcome = Assert.Single(result.Steps);
        var timing = outcome.Timing;

        // Zero, because the removal is timed on the executor's clock, which stood still through it.
        Assert.Equal(TimeSpan.Zero, Assert.IsType<RemovalPasses>(outcome.Passes).Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.UseCheck);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.Elapsed);
        Assert.Equal(TimeSpan.Zero, timing.Look);
        Assert.Equal(TimeSpan.Zero, timing.Acting);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);

        Assert.False(Directory.Exists(obj), "the directory the step named was left");
        Assert.True(File.Exists(sibling), "a file beside the directory was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A step the use check holds back still says how long the check took.</summary>
    [Fact]
    public async Task AStepAUseCheckHeldBackSaysHowLongTheCheckTook()
    {
        var clock = new ManualTimeProvider();
        var obj = _temp.CreateDirectory("App", "obj");
        _temp.CreateFile(4096, "App", "obj", "project.assets.json");

        var check = new AdvancingUseCheck(clock, TimeSpan.FromSeconds(2), inUse: "devenv is working in App");
        var result = await Execute(new DeleteDirectoryStep(obj, "Build output") { UseCheck = check }, clock);
        var outcome = Assert.Single(result.Steps);

        Assert.True(Directory.Exists(obj), "a directory something is using was removed");
        Assert.Equal(TimeSpan.FromSeconds(2), outcome.Timing.UseCheck);
        Assert.Equal(TimeSpan.FromSeconds(2), outcome.Timing.Elapsed);
        Assert.Null(outcome.Passes);
    }

    /// <summary>
    /// A look at the whole tree is timed apart from the removal, and only where one is taken. The look
    /// walks the real disk, so this is measured on the real clock.
    /// </summary>
    [Fact]
    public async Task ALookIsTimedApartAndOnlyWhereOneIsTaken()
    {
        var whole = _temp.CreateDirectory("whole");
        _temp.CreateFile(10, "whole", "a.bin");
        var plain = _temp.CreateDirectory("plain");
        _temp.CreateFile(10, "plain", "a.bin");

        var looked = await Execute(new DeleteDirectoryStep(whole, "Whole") { IsIndivisible = true }, TimeProvider.System);
        var walked = await Execute(new DeleteDirectoryStep(plain, "Plain"), TimeProvider.System);

        var lookTiming = Assert.Single(looked.Steps).Timing;
        Assert.True(lookTiming.Look > TimeSpan.Zero, "the look at an indivisible tree was not timed");
        Assert.True(lookTiming.Look <= lookTiming.Elapsed, "the look took longer than its step");
        Assert.Equal(TimeSpan.Zero, Assert.Single(walked.Steps).Timing.Look);
    }

    /// <summary>
    /// A removal's passes are inside its step, so together they take no longer than what the step spent
    /// apart from its checks, and each counts the entries it handled.
    /// </summary>
    [Fact]
    public async Task ARemovalsPassesSumToNoMoreThanItsStep()
    {
        var cache = _temp.CreateDirectory("cache");
        _temp.CreateFile(10, "cache", "a", "1.bin");
        _temp.CreateFile(10, "cache", "a", "2.bin");
        _temp.CreateFile(10, "cache", "b", "3.bin");

        var result = await Execute(new DeleteDirectoryStep(cache, "A cache"), TimeProvider.System);
        var outcome = Assert.Single(result.Steps);
        var passes = Assert.IsType<RemovalPasses>(outcome.Passes);

        Assert.True(
            passes.Elapsed <= outcome.Timing.Acting,
            $"the passes took {passes.Elapsed}, longer than the {outcome.Timing.Acting} their step spent removing");
        Assert.Equal(6, passes.Walk.Entries);
        Assert.Equal(3, passes.Files.Entries);
        Assert.Equal(3, passes.Folders.Entries);
        Assert.True(result.Duration >= outcome.Timing.Elapsed, "the plan took less time than its one step");
    }

    /// <summary>
    /// Each pass is timed over its own work and nothing else's. The clock moves a millisecond for each
    /// listing, a second for each file and a minute for each folder, so a pass that took in its
    /// neighbour's work shows it in a different unit.
    /// </summary>
    [Fact]
    public async Task EachPassIsTimedOverItsOwnWork()
    {
        var clock = new ManualTimeProvider();
        var root = _temp.CreateDirectory("tree");
        _temp.CreateFile(10, "tree", "a", "1.bin");
        _temp.CreateFile(10, "tree", "a", "2.bin");
        _temp.CreateFile(10, "tree", "b", "3.bin");
        _temp.CreateFile(10, "tree", "4.bin");
        var fs = new ClockedFileSystem(WindowsFileSystem.Default, clock);

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, CancellationToken.None, fs, time: clock);
        var passes = Assert.IsType<RemovalPasses>(outcome.Passes);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(new PassTiming(TimeSpan.FromMilliseconds(fs.Listings), 7), passes.Walk);
        Assert.Equal(new PassTiming(TimeSpan.FromSeconds(4), 4), passes.Files);
        Assert.Equal(new PassTiming(TimeSpan.FromMinutes(3), 3), passes.Folders);
    }

    /// <summary>
    /// A removal cancelled in its file pass reports the time that pass ran and the files it handled,
    /// and a folder pass that never began.
    /// </summary>
    [Fact]
    public async Task ARemovalCancelledInItsFilePassTimesWhatRan()
    {
        var clock = new ManualTimeProvider();
        var root = _temp.CreateDirectory("tree");

        // Far more files than a removal deletes at once, so the pass is cancelled with files untried.
        const int Files = 400;
        for (var i = 0; i < Files; i++)
        {
            _temp.CreateFile(1, "tree", $"d{i % 4}", $"{i}.bin");
        }

        using var cts = new CancellationTokenSource();
        var fs = new ClockedFileSystem(new CancellingFileSystem(WindowsFileSystem.Default, cts), clock);

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, cts.Token, fs, time: clock);
        var passes = Assert.IsType<RemovalPasses>(outcome.Passes);

        Assert.True(outcome.Interrupted);
        Assert.Equal(new PassTiming(TimeSpan.FromSeconds(fs.FileDeletions), fs.FileDeletions), passes.Files);
        Assert.InRange(passes.Files.Entries, 1, Files - 1);
        Assert.Equal(default, passes.Folders);
    }

    private Task<CleanupResult> Execute(CleanupStep step, TimeProvider time) =>
        new PlanExecutor(
                new FakeProcessRunner(),
                ParallelEnumerationScanner.Default,
                RefusalRecord.For(new FakeUserEnvironment(_temp.Path)),
                time: time)
            .ExecuteAsync(
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
                underway: null,
                ct: CancellationToken.None);

    /// <summary>A use check that takes <paramref name="takes"/> on the clock, and finds the step's own path in use where told to.</summary>
    private sealed class AdvancingUseCheck(ManualTimeProvider clock, TimeSpan takes, string? inUse = null) : IUseCheck
    {
        public IReadOnlyList<InUseNow> Ask(DeleteStep step, CancellationToken ct)
        {
            clock.Advance(takes);
            return inUse is null ? [] : [new InUseNow(step.Path, inUse)];
        }
    }

    /// <summary>
    /// The real filesystem, moving the clock a millisecond for each listing, a second for each file
    /// deleted and a minute for each folder.
    /// </summary>
    private sealed class ClockedFileSystem(IFileSystem inner, ManualTimeProvider clock) : IFileSystem
    {
        private int _listings;
        private int _fileDeletions;

        public int Listings => Volatile.Read(ref _listings);

        public int FileDeletions => Volatile.Read(ref _fileDeletions);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public PathPresence ProbeDirectory(string path) => inner.ProbeDirectory(path);

        public PathPresence ProbeEntry(string path) => inner.ProbeEntry(path);

        public bool IsReparsePoint(string path) => inner.IsReparsePoint(path);

        public ListingEnd ListEntries(string directory, ListingRequest request, List<WalkEntry> into)
        {
            Interlocked.Increment(ref _listings);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return inner.ListEntries(directory, request, into);
        }

        public long? TryGetFileLength(string path) => inner.TryGetFileLength(path);

        public long? TryGetNewestFileTime(string path) => inner.TryGetNewestFileTime(path);

        public void DeleteFile(string path)
        {
            Interlocked.Increment(ref _fileDeletions);
            clock.Advance(TimeSpan.FromSeconds(1));
            inner.DeleteFile(path);
        }

        public RefusalReason? ProbeRemoval(string path) => inner.ProbeRemoval(path);

        public void DeleteDirectory(string path)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            inner.DeleteDirectory(path);
        }

        public void ClearAttributes(string path) => inner.ClearAttributes(path);

        public FileAttributes? TryGetAttributes(string path) => inner.TryGetAttributes(path);

        public bool MayExist(string path) => inner.MayExist(path);

        public string? FinalPath(string path) => inner.FinalPath(path);
    }
}
