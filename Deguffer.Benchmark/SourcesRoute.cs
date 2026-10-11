using System.Collections.Frozen;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Benchmark;

/// <summary>
/// The search for build outputs in an approved source folder, as a scan that cannot read the file
/// table makes it.
///
/// <para>It visits fewer folders than <see cref="WalkRoute"/>: it stops at each folder it finds and
/// never enters <c>.git</c> or <c>node_modules</c>, so the bare walk of the same folder says little
/// about what the search costs.</para>
/// </summary>
internal static class SourcesRoute
{
    /// <summary>
    /// Every name the source folder rows look for, registered by building those rows exactly as a scan
    /// builds them, so the search here is never for a set that has drifted from theirs.
    /// </summary>
    public static FrozenSet<string> Names { get; } = Registered();

    /// <returns>
    /// The candidates found, and complete where no folder in the tree refused to be listed.
    /// </returns>
    public static RunTally Run(string folder, WalkTuning tuning, CancellationToken ct)
    {
        var walked = SourceTreeWalk.Under(folder, Names, tuning, ct);

        return new RunTally(walked.Candidates.Count, BytesRead: 0, Complete: walked.Unreadable.Count == 0);
    }

    private static FrozenSet<string> Registered()
    {
        var environment = UserEnvironment.Current;
        var discovery = new SourceDirectoryDiscovery(ParallelEnumerationScanner.Default);

        CleanupPlanner.SourceTreeProviders(
            new SourceRootStore(environment),
            discovery,
            LiveTreeInspector.Default,
            environment,
            ParallelEnumerationScanner.Default);

        return discovery.Names();
    }
}
