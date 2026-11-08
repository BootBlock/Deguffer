using Deguffer.Core.Cloud;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A release step of many files, asked about several at a time and proved standing the same way.
///
/// <para>Each request and each reading here is held until a set of them is under way, so the tests
/// prove that the work runs side by side by the order things happened in, never by how long it took.
/// Run one at a time, the first held call waits for a second that cannot begin until it ends, gives
/// up at the deadline, and the test fails on the count it saw.</para>
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
    /// </summary>
    [Fact]
    public async Task ReadsWhatEachFolderPassesOnOnceWhateverAsksAtOnce()
    {
        var step = Step(LocalCopyRelease.ReleasesAtOnce * 4);
        var folders = step.Files.Select(f => Path.GetDirectoryName(f.Path)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Each folder's reading is held a while, in which a second thread asking about it would read it too.
        _cloud.BeforeRead = path =>
        {
            if (folders.Contains(path) || string.Equals(path, _root, StringComparison.OrdinalIgnoreCase))
            {
                lock (reads)
                {
                    reads[path] = reads.GetValueOrDefault(path) + 1;
                }

                Thread.Sleep(50);
            }
        };

        await Run(step);

        Assert.Equal([.. folders.Append(_root).Order(StringComparer.OrdinalIgnoreCase)], reads.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(reads, read => Assert.Equal(1, read.Value));
    }

    /// <summary>Reported from whichever thread finished a file, and still never backwards.</summary>
    [Fact]
    public async Task ReportsProgressThatNeverMovesBackwardsAndEndsWhole()
    {
        var step = Step(4_000);
        var progress = new ProgressRecorder<double>();

        await Run(step, progress);

        var reports = progress.Reports;
        Assert.True(reports.Count > 2, $"Only {reports.Count} report(s) were made.");
        Assert.Equal(1.0, reports[^1]);
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.First <= pair.Second, $"{pair.First} came before {pair.Second}."));
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
