using System.Collections.Concurrent;
using Deguffer.Core.Cloud;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.6's two reads of a clean's protected paths, the one before its first deletion
/// (<see cref="RunResidue.BeginAsync(IReadOnlyList{CleanupPlan}, ScanTuner)"/>) and the one after each
/// plan (<see cref="PlanVerifier.Verify(CleanupPlan, RunReach?, RunResidue?, CancellationToken, ICloudFiles?, SystemProtection.ISystemProtection?, IVolumeInventory?, Profiles.IUserProfiles?, ScanTuner?)"/>),
/// probe several paths at once and still answer each path as it was answered alone, in the plan's order.
///
/// <para>The paths that are never touched are on a share of their own, so no walk another test runs
/// shares their volume's slots. Each wait is bounded, so a read that probes one path at a time fails on
/// its assertion rather than hanging.</para>
/// </summary>
public sealed class ProofProbesTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A path whose probe waits until a second path is being probed is still answered as present.
    /// Probed one at a time, the first waits for a second that never starts, and is read as gone.
    /// </summary>
    [Fact]
    public async Task TheRunsFirstReadProbesSeveralPathsAtOnce()
    {
        using var both = new Barrier(2);
        var plan = Plan(Protect(Invented("begin", "a")), Protect(Invented("begin", "b")));

        var residue = await RunResidue.BeginAsync(
            [plan], TunedTo(2), _ => both.SignalAndWait(Patience) ? PathPresence.Present : PathPresence.Absent);

        Assert.All(plan.ProtectedPaths, path => Assert.False(residue.WasGoneBeforeRun(path.Path)));
    }

    /// <summary>
    /// Across two plans, every path held standing is probed once: each protected path that was there
    /// and each file a release step named. Only an absent one is recorded as gone, never a refused one,
    /// and a path that was never there is not asked.
    /// </summary>
    [Fact]
    public async Task TheRunsFirstReadRecordsEveryAbsentPathAndOnlyThose()
    {
        var present = Invented("mixed", "present");
        var absent = Invented("mixed", "absent");
        var refused = Invented("mixed", "refused");
        var neverThere = Invented("mixed", "never");
        var releasedGone = Invented("mixed", "cloud", "gone.bin");
        var releasedHere = Invented("mixed", "cloud", "here.bin");

        var answers = new Dictionary<string, PathPresence>(StringComparer.OrdinalIgnoreCase)
        {
            [present] = PathPresence.Present,
            [absent] = PathPresence.Absent,
            [refused] = PathPresence.Refused,
            [releasedGone] = PathPresence.Absent,
            [releasedHere] = PathPresence.Present,
        };

        var asked = new ConcurrentBag<string>();
        var first = Plan(Protect(present), Protect(absent), new ProtectedPath(neverThere, "It must survive.", PathPresence.Absent));
        var second = Plan(Protect(refused)) with
        {
            Steps =
            [
                new ReleaseLocalCopiesStep(Invented("mixed", "cloud"), "Sync app", "Local copies")
                {
                    Files = [new ReleasableFile(releasedGone, 1), new ReleasableFile(releasedHere, 1)],
                },
            ],
        };

        var residue = await RunResidue.BeginAsync(
            [first, second],
            TunedTo(4),
            path =>
            {
                asked.Add(path);
                return answers[path];
            });

        Assert.Equal(answers.Keys.Order(StringComparer.OrdinalIgnoreCase), asked.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            [absent, releasedGone],
            answers.Keys.Append(neverThere).Where(residue.WasGoneBeforeRun).Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A protected path whose probe waits until a second path is being checked still survives. Checked
    /// one at a time, the first waits for a second that never starts, and is read as undescribed.
    /// </summary>
    [Fact]
    public void TheVerificationChecksSeveralProtectedPathsAtOnce()
    {
        using var both = new Barrier(2);
        var plan = Plan(Protect(Invented("verify", "a")), Protect(Invented("verify", "b")));

        var checks = Verify(plan, TunedTo(2), _ => both.SignalAndWait(Patience) ? PathPresence.Present : PathPresence.Refused);

        Assert.All(checks, check => Assert.Equal(VerificationOutcome.Survived, check.Outcome));
    }

    /// <summary>
    /// A plan protecting one path of each kind, checked several at a time, is answered for each path as
    /// it is when checked alone, and in the plan's order: a survivor, a path never there, a folder
    /// emptied, a folder marked for removal, one Windows will not describe, a link standing, and a path
    /// missing from a folder still standing. Run over every ordering of the paths, so no order the
    /// workers finish in can line the report up by chance.
    /// </summary>
    [Fact]
    public void TheVerificationAnswersEachPathInThePlansOrder()
    {
        var survivor = _temp.CreateDirectory("survivor");
        var emptied = _temp.CreateDirectory("emptied");
        var marked = _temp.CreateDirectory("marked");
        File.WriteAllBytes(Path.Combine(marked, ".delete"), []);
        var refused = _temp.CreateDirectory("refused");
        var target = _temp.CreateDirectory("target");
        var link = Path.Combine(_temp.Path, "link");
        DirectoryLink.Create(DirectoryLinkKind.Junction, link, target);
        var missing = Path.Combine(_temp.Path, "missing");

        (ProtectedPath Path, VerificationOutcome Outcome)[] kinds =
        [
            (Protect(survivor), VerificationOutcome.Survived),
            (new ProtectedPath(Path.Combine(_temp.Path, "never"), "It must survive.", PathPresence.Absent), VerificationOutcome.NotPresentBefore),
            (new ProtectedPath(emptied, "It must survive.", PathPresence.Present, HeldContentBefore: true), VerificationOutcome.Emptied),
            (new ProtectedPath(marked, "It must survive.", PathPresence.Present, Marker: ".delete"), VerificationOutcome.Failed),
            (Protect(refused), VerificationOutcome.Unverified),
            (Protect(link), VerificationOutcome.Survived),
            (Protect(missing), VerificationOutcome.Failed),
        ];

        foreach (var shift in Enumerable.Range(0, kinds.Length))
        {
            var ordered = kinds.Skip(shift).Concat(kinds.Take(shift)).ToList();

            var checks = Verify(
                Plan([.. ordered.Select(kind => kind.Path)]),
                TunedTo(kinds.Length),
                path => path == refused ? PathPresence.Refused : LongPath.ProbeEntry(path));

            Assert.Equal(
                ordered.Select(kind => (kind.Path.Path, kind.Outcome)),
                checks.Select(check => (check.Subject, check.Outcome)));
        }
    }

    private static IReadOnlyList<VerificationCheck> Verify(CleanupPlan plan, ScanTuner tuning, Func<string, PathPresence> probe) =>
        PlanVerifier.Verify(plan, null, null, null, null, new FakeVolumeInventory(), null, tuning, probe, CancellationToken.None).Checks;

    private static CleanupPlan Plan(params ProtectedPath[] protectedPaths) => new()
    {
        ProviderId = "test",
        ProviderName = "Test provider",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "It comes back.",
        ProtectedPaths = protectedPaths,
    };

    private static ProtectedPath Protect(string path) => new(path, "It must survive.", PathPresence.Present);

    private static string Invented(params string[] parts) => Path.Combine([@"\\proof-probes.test\share", .. parts]);

    /// <summary>
    /// Walks of every kind of drive tuned to <paramref name="threads"/>, so the paths on the suite's own
    /// disk are checked as many at once as the invented ones, whatever disk the suite runs on.
    /// </summary>
    private static ScanTuner TunedTo(int threads)
    {
        var chosen = new MediaScanPreferences(WalkThreads: threads);
        var scanning = Enum.GetValues<StorageMedia>().Aggregate(ScanPreferences.Default, (preferences, media) => preferences.With(media, chosen));

        return new ScanTuner(new FakePreferences(AppPreferences.Default with { Scanning = scanning }));
    }
}
