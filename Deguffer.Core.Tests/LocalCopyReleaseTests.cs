using System.Collections.Concurrent;
using Deguffer.Core.Cloud;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A release step of many files, asked about several at a time and proved standing the same way.
///
/// <para>A test that the work runs side by side holds each call until a set of them is under way, so
/// it proves it by the order things happened in, never by how long the work took. Run one at a time,
/// the first held call waits for a second that cannot begin until it ends, gives up at the deadline,
/// and the test fails on the count it saw. The bound's ceiling is the one timed window: a held set is
/// kept a little longer once it has gathered, in which a call past the bound would have begun too.
/// </para>
/// </summary>
public sealed class LocalCopyReleaseTests : IDisposable
{
    private const string OneDriveId = "OneDrive!S-1-5-21-1111111111-2222222222-3333333333-1001!Personal";

    private readonly TempDirectory _temp = new();
    private readonly FakeCloudFiles _cloud = new();
    private readonly string _root;

    public LocalCopyReleaseTests()
    {
        // A real folder, so a file missing from the proof is judged against a folder still standing.
        _root = _temp.CreateDirectory("OneDrive");
        _cloud.Root(OneDriveId, _root, "OneDrive - Personal");
    }

    public void Dispose() => _temp.Dispose();

    private string At(params string[] segments) => Path.Combine([_root, .. segments]);

    /// <summary>
    /// A step naming <paramref name="files"/> placeholders of 100 bytes each, spread over a few folders that
    /// are really on the disk, as the proof asks of a file it finds missing.
    /// </summary>
    private ReleaseLocalCopiesStep Step(int files)
    {
        List<ReleasableFile> named = [];

        for (var i = 0; i < files; i++)
        {
            var folder = At($"Folder {i % 5}");
            var path = Path.Combine(folder, $"file {i:D5}.txt");

            Directory.CreateDirectory(folder);
            _cloud.Folder(folder).File(path, onDisk: 100);
            named.Add(new ReleasableFile(path, 100));
        }

        return new ReleaseLocalCopiesStep(_root, "OneDrive", "Local copies") { Files = named };
    }

    private Task<StepOutcome> Run(ReleaseLocalCopiesStep step, IProgress<double>? progress = null) =>
        LocalCopyRelease.RunAsync(_cloud, step, MinimumAge.Off, progress, CancellationToken.None);

    private static CleanupPlan PlanOf(ReleaseLocalCopiesStep step) => new()
    {
        ProviderId = "cloud-local-copies",
        ProviderName = "Local copies of cloud files",
        Tier = SafetyTier.RegenerableWithCost,
        WhatHappensOnNextUse = "Each file downloads again when it is opened.",
        Steps = [step],
    };

    private VerificationResult Verify(ReleaseLocalCopiesStep step) =>
        PlanVerifier.Verify(PlanOf(step), cloud: _cloud, volumes: new FakeVolumeInventory());

    [Fact]
    public async Task AsksAboutTheBoundsWorthOfFilesAtOnceAndNoMore()
    {
        var step = Step(LocalCopyRelease.ReleasesAtOnce * 4);
        using var held = new HeldCalls(LocalCopyRelease.ReleasesAtOnce);
        _cloud.BeforeRelease = _ => held.Hold();

        await Run(step);

        Assert.Equal(LocalCopyRelease.ReleasesAtOnce, held.MostAtOnce);
    }

    /// <summary>
    /// However the threads interleave, every file the step named is asked about exactly once, and the
    /// tally is the sum of every answer: a request counted twice or lost would move a figure here.
    /// </summary>
    [Fact]
    public async Task AsksAboutEveryFileOnceAndTalliesEveryAnswer()
    {
        var step = Step(1_000);
        var gone = step.Files.Where((_, i) => i % 11 == 1).Select(f => f.Path).ToHashSet();
        var edited = step.Files.Where((_, i) => i % 7 == 0).Select(f => f.Path).Except(gone).ToHashSet();

        foreach (var path in edited)
        {
            _cloud.Change(path, p => p with { ModifiedBytes = 5, InSync = false });
        }

        foreach (var path in gone)
        {
            _cloud.Remove(path);
        }

        var outcome = await Run(step);

        Assert.Equal(step.Files.Select(f => f.Path).Order(), _cloud.Asked.Order());
        var released = step.Files.Select(f => f.Path).Where(p => !edited.Contains(p) && !gone.Contains(p)).ToList();
        Assert.Equal(released.Order(), _cloud.Unpinned.Order());
        Assert.Equal(released.Count * 100L, outcome.BytesRequested);
        Assert.Contains($"{edited.Count:N0} file(s) had changed since the scan", outcome.Message, StringComparison.Ordinal);
        Assert.Contains($"{gone.Count:N0} file(s) were no longer there", outcome.Message, StringComparison.Ordinal);
        Assert.All(edited, path => Assert.Equal(PinState.Unspecified, _cloud.PlaceholderAt(path)!.Pin));
    }

    /// <summary>
    /// Every thread asks what the folders pass on, and each folder is still read from the disk once:
    /// a thread arriving while another reads it waits for that reading rather than making its own.
    /// Each folder's first reading is held until a second reading of it begins, which only a thread
    /// making its own does, or until the patience runs out.
    /// </summary>
    [Fact]
    public async Task ReadsWhatEachFolderPassesOnOnceWhateverAsksAtOnce()
    {
        var step = Step(LocalCopyRelease.ReleasesAtOnce * 4);
        var folders = step.Files.Select(f => Path.GetDirectoryName(f.Path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        _cloud.BeforeRead = path =>
        {
            if ((folders.Contains(path) || string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
                && reads.AddOrUpdate(path, 1, static (_, count) => count + 1) == 1)
            {
                SpinWait.SpinUntil(() => reads[path] > 1, TimeSpan.FromSeconds(1));
            }
        };

        await Run(step);

        Assert.Equal([.. folders.Append(_root).Order(StringComparer.OrdinalIgnoreCase)], reads.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(reads, read => Assert.Equal(1, read.Value));
    }

    /// <summary>
    /// Reported every so often from whichever thread finished a file, and whole at the end. That two
    /// such reports reach the bar in order is <see cref="ForwardProgress"/>'s, and proved there.
    /// </summary>
    [Fact]
    public async Task ReportsProgressAsFilesFinishAndEndsWhole()
    {
        var step = Step(4_000);
        var progress = new ProgressRecorder<double>();

        await Run(step, progress);

        var reports = progress.Reports;
        Assert.True(reports.Count > 2, $"Only {reports.Count} report(s) were made.");
        Assert.All(reports, report => Assert.InRange(report, double.Epsilon, 1.0));
        Assert.Equal(1.0, reports[^1]);
    }

    /// <summary>
    /// Neither the requests nor the proof's readings run on the pool, through the clean and through a
    /// verification asked for on its own: each blocks its thread on the disk, and the pool's threads
    /// are what the rest of the process's async work waits for.
    /// </summary>
    [Fact]
    public async Task ReleasesAndProvesNoFileOnThePool()
    {
        var provider = new CloudLocalCopiesProvider(
            _cloud,
            new FakeUserEnvironment(_temp.Path),
            new FakeProcessRunner(),
            FakeProcessInspector.NothingRunning,
            new FakeDirectoryScanner());
        var step = Step(LocalCopyRelease.ReleasesAtOnce * 4);
        var plan = await provider.PlanAsync();
        Assert.Equal(step.Files.Count, Assert.IsType<ReleaseLocalCopiesStep>(Assert.Single(plan.Steps)).Files.Count);

        var named = step.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var released = 0;
        var read = 0;
        var onThePool = 0;

        void Watch(ref int calls)
        {
            Interlocked.Increment(ref calls);

            if (Thread.CurrentThread.IsThreadPoolThread)
            {
                Interlocked.Increment(ref onThePool);
            }

            // Long enough that every thread of the run takes some of the files, its calling thread too.
            Thread.Sleep(2);
        }

        _cloud.BeforeRelease = _ => Watch(ref released);
        _cloud.BeforeRead = path =>
        {
            if (named.Contains(path))
            {
                Watch(ref read);
            }
        };

        var result = await provider.ExecuteAsync(plan);
        // Asked from the pool, as an async caller does once it has resumed there, so the proof's own
        // calling thread is only off the pool where the verification starts it off the pool.
        var verified = await Task.Run(() => provider.VerifyAsync(plan));

        Assert.True(result.Verification!.Passed);
        Assert.True(verified.Passed);
        Assert.Equal(step.Files.Count, released);
        Assert.Equal(2 * step.Files.Count, read);
        Assert.Equal(0, onThePool);
    }

    [Fact]
    public void ReadsTheBoundsWorthOfReleasedFilesAtOnceForTheProof()
    {
        var step = Step(LocalCopyRelease.ReleasesAtOnce * 4);
        using var held = new HeldCalls(LocalCopyRelease.ReleasesAtOnce);
        var named = step.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _cloud.BeforeRead = path =>
        {
            if (named.Contains(path))
            {
                held.Hold();
            }
        };

        Verify(step);

        Assert.Equal(LocalCopyRelease.ReleasesAtOnce, held.MostAtOnce);
    }

    /// <summary>
    /// §5.6: the proof names every released file in the order the step named them, though the first is
    /// the last to be read, and a file gone during the run is still raised as the alarm it is.
    /// </summary>
    [Fact]
    public void ProvesEveryReleasedFileInTheStepsOrderAndRaisesTheOneThatWent()
    {
        var step = Step(200);
        var ordered = step with { Files = [.. step.Files.Reverse()] };
        var first = ordered.Files[0].Path;
        var lost = ordered.Files[57].Path;
        _cloud.Remove(lost);

        using var othersRead = new CountdownEvent(ordered.Files.Count - 1);
        _cloud.BeforeRead = path =>
        {
            if (string.Equals(path, first, StringComparison.OrdinalIgnoreCase))
            {
                othersRead.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                othersRead.Signal();
            }
        };

        var verification = Verify(ordered);

        var checks = verification.Checks.Where(c => c.Subject != _root).ToList();
        Assert.Equal(ordered.Files.Select(f => f.Path), checks.Select(c => c.Subject));
        Assert.Equal(VerificationOutcome.Failed, Assert.Single(checks, c => c.Subject == lost).Outcome);
        Assert.All(checks.Where(c => c.Subject != lost), c => Assert.Equal(VerificationOutcome.Survived, c.Outcome));
        Assert.False(verification.Passed);
    }

    /// <summary>
    /// Calls counted while they are under way, each held until <c>together</c> of them are, and then a
    /// little longer, in which a call past the bound would have begun too. A set that never gathers is
    /// let go at the deadline, so <see cref="MostAtOnce"/> shows what it reached rather than the test
    /// hanging.
    /// </summary>
    private sealed class HeldCalls(int together) : IDisposable
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly ManualResetEventSlim _gathered = new();
        private int _running;
        private int _mostAtOnce;

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public void Dispose() => _gathered.Dispose();

        public void Hold()
        {
            var running = Interlocked.Increment(ref _running);

            for (var most = Volatile.Read(ref _mostAtOnce); running > most; most = Volatile.Read(ref _mostAtOnce))
            {
                Interlocked.CompareExchange(ref _mostAtOnce, running, most);
            }

            if (running >= together)
            {
                _gathered.Set();
            }

            // Gathered once is gathered for good, and a set that timed out is not waited for again, so
            // a run one call at a time costs one deadline rather than one per call.
            if (!_gathered.Wait(Deadline))
            {
                _gathered.Set();
            }

            Thread.Sleep(20);
            Interlocked.Decrement(ref _running);
        }
    }
}
