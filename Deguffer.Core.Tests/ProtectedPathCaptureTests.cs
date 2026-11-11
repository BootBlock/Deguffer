using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6's record of what each protected path was before a run, taken for many paths at once: the same
/// record, path for path and in the same order, as the paths asked one after another give, and taken
/// with the probes overlapping, so a row protecting thousands of paths does not pay each wait in turn.
///
/// <para>The tests of overlap and order use invented paths on a share of their own and a probe of the
/// test's own, so no walk another test runs shares their volume's slots. Each wait is bounded, so a
/// capture that probes one path at a time fails on its assertion rather than hanging.</para>
/// </summary>
public sealed class ProtectedPathCaptureTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Every shape a protected path takes is recorded as asking it alone records it: a folder holding a
    /// file, one holding only empty folders, an empty one, a file, a link, a path that is not there, and
    /// one Windows will not describe, which is refused and never absent.
    /// </summary>
    [Fact]
    public async Task EachPathIsRecordedAsAskingItAloneRecordsIt()
    {
        var holding = _temp.CreateDirectory("holding");
        _temp.CreateFile(8, "holding", "deep", "one.bin");

        var skeleton = _temp.CreateDirectory("skeleton");
        _temp.CreateDirectory("skeleton", "a", "b");
        var empty = _temp.CreateDirectory("empty");
        var file = _temp.CreateFile(8, "one.bin");
        var absent = Path.Join(_temp.Path, "never-existed");

        var link = Path.Join(_temp.Path, "link");
        Junction.ToDirectory(link, holding);

        var refused = _temp.CreateDirectory("holder", "refused");

        IReadOnlyList<(string Path, string Reason)> candidates =
        [
            (holding, "holding"),
            (skeleton, "skeleton"),
            (empty, "empty"),
            (file, "file"),
            (link, "link"),
            (absent, "absent"),
            (refused, "refused"),
        ];

        using var denied = DeniedDirectory.WithUnreadableAttributes(refused);

        var captured = await ProtectedPathCapture.CaptureAsync(candidates, ScanTuner.Shipped, default);

        Assert.Equal(
            [.. candidates.Select(c => new ProtectedPath(c.Path, c.Reason, LongPath.ProbeEntry(c.Path), DirectoryContent.IsPresent(c.Path)))],
            captured);

        // The answers the comparison rests on, so a probe that answered every path alike could not pass it.
        Assert.Equal(
            [
                (PathPresence.Present, true),
                (PathPresence.Present, false),
                (PathPresence.Present, false),
                (PathPresence.Present, false),
                (PathPresence.Present, true),
                (PathPresence.Absent, false),
                (PathPresence.Refused, false),
            ],
            captured.Select(path => (path.PresenceBefore, path.HeldContentBefore)));
    }

    /// <summary>
    /// A path named twice is recorded once, under the first reason given for it, and probed once. A
    /// provider names its own paths first and the children it spared after them, and the two overlap.
    /// </summary>
    [Fact]
    public async Task APathNamedTwiceIsRecordedOnceUnderItsFirstReason()
    {
        var probed = new List<string>();

        var captured = await ProtectedPathCapture.CaptureAsync(
            [(@"\\volume.test\cache\Tool", "The tool's root."), (@"\\volume.test\cache\tool", "A spared child.")],
            ScanTuner.Shipped,
            path =>
            {
                lock (probed)
                {
                    probed.Add(path);
                }

                return new ProtectedPathCapture.Before(PathPresence.Present, HeldContent: true);
            },
            default);

        var only = Assert.Single(captured);
        Assert.Equal(@"\\volume.test\cache\Tool", only.Path);
        Assert.Equal("The tool's root.", only.Reason);
        Assert.Equal([@"\\volume.test\cache\Tool"], probed);
    }

    /// <summary>
    /// The probes overlap: a path whose probe waits until a second path is being probed still gets its
    /// answer. Probed one after another, the first would wait for a second that never starts.
    /// </summary>
    [Fact]
    public async Task TheProbesOverlap()
    {
        using var both = new Barrier(2);

        var captured = await ProtectedPathCapture.CaptureAsync(
            Candidates(2),
            TunedTo(2),
            _ => new ProtectedPathCapture.Before(both.SignalAndWait(Patience) ? PathPresence.Present : PathPresence.Absent, HeldContent: false),
            default);

        Assert.All(captured, path => Assert.Equal(PathPresence.Present, path.PresenceBefore));
    }

    /// <summary>
    /// Each record carries its own path's answers, in the order the paths were given, though each probe
    /// finishes only after the one given after it has. Paired by position, a record out of place would
    /// say that one folder held content, or was there at all, on the evidence of another.
    /// </summary>
    [Fact]
    public async Task EachRecordCarriesItsOwnPathsAnswersWhenTheProbesFinishInReverse()
    {
        const int Count = 6;

        var candidates = Candidates(Count);
        var finished = Enumerable.Range(0, Count).Select(_ => new ManualResetEventSlim()).ToArray();

        try
        {
            var captured = await ProtectedPathCapture.CaptureAsync(
                candidates,
                TunedTo(Count),
                path =>
                {
                    var index = IndexOf(candidates, path);
                    var waited = index == Count - 1 || finished[index + 1].Wait(Patience);

                    finished[index].Set();

                    // Present with content on the even paths only, so a record paired with its neighbour's answer differs.
                    return waited
                        ? new ProtectedPathCapture.Before(index % 2 == 0 ? PathPresence.Present : PathPresence.Absent, HeldContent: index % 2 == 0)
                        : new ProtectedPathCapture.Before(PathPresence.Refused, HeldContent: false);
                },
                default);

            Assert.Equal(candidates.Select(c => c.Path), captured.Select(path => path.Path));
            Assert.Equal(candidates.Select(c => c.Reason), captured.Select(path => path.Reason));
            Assert.Equal(
                Enumerable.Range(0, Count).Select(index => index % 2 == 0 ? (PathPresence.Present, true) : (PathPresence.Absent, false)),
                captured.Select(path => (path.PresenceBefore, path.HeldContentBefore)));
        }
        finally
        {
            foreach (var done in finished)
            {
                done.Dispose();
            }
        }
    }

    /// <summary>A cancelled plan probes nothing and records nothing.</summary>
    [Fact]
    public async Task ACancelledCaptureProbesNothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var probed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProtectedPathCapture.CaptureAsync(
            Candidates(4),
            ScanTuner.Shipped,
            _ =>
            {
                Interlocked.Increment(ref probed);
                return new ProtectedPathCapture.Before(PathPresence.Present, HeldContent: false);
            },
            cancelled.Token));

        Assert.Equal(0, probed);
    }

    private static List<(string Path, string Reason)> Candidates(int count) =>
        [.. Enumerable.Range(0, count).Select(index => ($@"\\volume.test\claude\projects\session{index}", $"Conversation {index}."))];

    private static int IndexOf(List<(string Path, string Reason)> candidates, string path) =>
        candidates.FindIndex(c => c.Path == path);

    /// <summary>A walk of <paramref name="threads"/> on every volume: the tuner knows no drive's kind, so each is unknown.</summary>
    private static ScanTuner TunedTo(int threads) => new(new FakePreferences(AppPreferences.Default with
    {
        Scanning = ScanPreferences.Default.With(StorageMedia.Unknown, new MediaScanPreferences(WalkThreads: threads)),
    }));
}
