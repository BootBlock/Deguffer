using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// Many empty folders proved standing as §5.6 proves the paths a plan protects, after its removals: what
/// the result of a clean that protects one path for each item of the user's data waits on.
///
/// <para><b>Built once, then only read.</b> The folders are made before the first run, and every run
/// verifies one plan that protects them all and names nothing to remove, with the walk a scan uses for
/// the drive's kind. Each folder is recorded as there before the run and holding nothing, so each check
/// costs its probe.</para>
///
/// <para><b>The answers are checked after each run</b>, outside its time: every folder survived. A
/// verification that answered wrongly could otherwise be timed as a faster one.</para>
/// </summary>
internal sealed class VerifyRoute
{
    private readonly RemovalSite _site;
    private readonly ScanTuner _tuning;
    private readonly CleanupPlan _plan;
    private bool _built;
    private VerificationResult? _verified;

    /// <param name="folders">How many empty folders are built and proved standing.</param>
    /// <param name="tuning">What the walks of each volume run with, as a scan's do.</param>
    public VerifyRoute(RemovalSite site, int folders, ScanTuner tuning)
    {
        _site = site;
        _tuning = tuning;
        _plan = new CleanupPlan
        {
            ProviderId = "benchmark",
            ProviderName = "Benchmark",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing: the plan removes nothing.",
            ProtectedPaths =
            [
                .. Enumerable.Range(0, folders).Select(i => new ProtectedPath(
                    site.Place($"f{i}"), "A folder the benchmark built to prove standing.", PathPresence.Present)),
            ],
        };
    }

    public RouteRun Timed => RouteRun.Blocking(Run, Build, Check);

    private void Build(CancellationToken ct)
    {
        if (_built)
        {
            return;
        }

        foreach (var folder in _plan.ProtectedPaths)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(LongPath.Extended(folder.Path));
        }

        _built = true;
    }

    private RunTally Run(CancellationToken ct)
    {
        var probes = new InFlightPeak();

        _verified = PlanVerifier.Verify(
            _plan,
            runReach: null,
            residue: null,
            new VerificationSources(Volumes: VolumeInventory.Current, Tuning: _tuning),
            folder =>
            {
                using var probe = probes.Enter();

                return LongPath.ProbeEntry(folder);
            },
            ct);

        return new RunTally(
            _verified.Checks.Count, BytesRead: 0, Complete: _verified.Checks.Count == _plan.ProtectedPaths.Count, PeakListings: probes.Peak);
    }

    /// <summary>
    /// Every folder proved standing, and the site's kept file still standing. Thrown rather than counted,
    /// because a wrong answer is a defect to stop on, not a slow run.
    /// </summary>
    private void Check()
    {
        _site.ProveBesideStanding();

        if (_verified is not { } verified || verified.Checks.Any(check => check.Outcome is not VerificationOutcome.Survived))
        {
            throw new InvalidOperationException("The verification did not prove a folder the benchmark built standing (§5.6).");
        }
    }
}
