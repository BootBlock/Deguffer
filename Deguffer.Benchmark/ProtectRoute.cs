using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// Many empty folders recorded as §5.6 records the paths a plan protects, before a run: what a row that
/// protects one path for each item of the user's data spends on its protected paths, such as the
/// Claude Code rows' thousands of conversation and session folders.
///
/// <para><b>Built once, then only read.</b> The folders are made before the first run, and every run
/// asks each one whether it is there and whether it holds anything, with the walk a scan uses for the
/// drive's kind. Each folder is empty, so the content question costs its one listing.</para>
///
/// <para><b>The answers are checked after each run</b>, outside its time: every folder there and
/// holding nothing. A capture that answered wrongly could otherwise be timed as a faster one.</para>
/// </summary>
internal sealed class ProtectRoute
{
    private readonly RemovalSite _site;
    private readonly ScanTuner _tuning;
    private readonly IReadOnlyList<(string Path, string Reason)> _folders;
    private bool _built;
    private IReadOnlyList<ProtectedPath>? _captured;

    /// <param name="folders">How many empty folders are built and recorded.</param>
    /// <param name="tuning">What the walks of each volume run with, as a scan's do.</param>
    public ProtectRoute(RemovalSite site, int folders, ScanTuner tuning)
    {
        _site = site;
        _tuning = tuning;
        _folders = [.. Enumerable.Range(0, folders).Select(i => (site.Place($"f{i}"), "A folder the benchmark built to record."))];
    }

    public RouteRun Timed => new(Run, Build, Check);

    private void Build(CancellationToken ct)
    {
        if (_built)
        {
            return;
        }

        foreach (var (folder, _) in _folders)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(LongPath.Extended(folder));
        }

        _built = true;
    }

    private RunTally Run(CancellationToken ct)
    {
        var probes = new InFlightPeak();

        _captured = ProtectedPathCapture.CaptureAsync(
            _folders,
            _tuning,
            folder =>
            {
                using var probe = probes.Enter();

                return ProtectedPathCapture.Probe(folder);
            },
            ct).GetAwaiter().GetResult();

        return new RunTally(_captured.Count, BytesRead: 0, Complete: _captured.Count == _folders.Count, PeakListings: probes.Peak);
    }

    /// <summary>
    /// Every folder recorded as there and holding nothing, and the site's kept file still standing.
    /// Thrown rather than counted, because a wrong record is a defect to stop on, not a slow run.
    /// </summary>
    private void Check()
    {
        _site.ProveBesideStanding();

        if (_captured!.Any(path => path.PresenceBefore is not PathPresence.Present || path.HeldContentBefore))
        {
            throw new InvalidOperationException("The capture recorded an empty folder the benchmark built as missing or as holding something (§5.6).");
        }
    }
}
