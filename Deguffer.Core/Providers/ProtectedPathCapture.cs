using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// §5.6's record of what each protected path was before a run, taken for many paths at once.
///
/// <para><b>Several at once, under the disk's own number</b> (<see cref="DiskBoundWork"/>). Each path
/// costs a handful of directory operations and depends on no other, and a row that protects one path
/// for each item of the user's data protects thousands. Probed one after another, those waits were most
/// of the row's planning time: seconds on a row that ends late in the pass. A spinning disk tuned to
/// one thread is still probed one path at a time.</para>
///
/// <para><b>What each path proves is unchanged.</b> Every path is asked the same two questions it was
/// asked one at a time, and the answers come back in the order the paths were given.</para>
/// </summary>
internal static class ProtectedPathCapture
{
    /// <summary>
    /// What each of <paramref name="candidates"/> was, in their order, each path named once under the
    /// first reason given for it.
    /// </summary>
    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    public static Task<IReadOnlyList<ProtectedPath>> CaptureAsync(
        IReadOnlyList<(string Path, string Reason)> candidates,
        ScanTuner tuning,
        CancellationToken ct) =>
        CaptureAsync(candidates, tuning, Probe, ct);

    /// <summary>The same, with each path's two answers given by <paramref name="probe"/>: the seam a test blocks a probe through.</summary>
    internal static async Task<IReadOnlyList<ProtectedPath>> CaptureAsync(
        IReadOnlyList<(string Path, string Reason)> candidates,
        ScanTuner tuning,
        Func<string, Before> probe,
        CancellationToken ct)
    {
        (string Path, string Reason)[] named = [.. candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];

        var found = await DiskBoundWork.EachAsync([.. named.Select(c => c.Path)], tuning, probe, ct).ConfigureAwait(false);

        return [.. named.Zip(found, (c, before) => new ProtectedPath(c.Path, c.Reason, before.Presence, before.HeldContent))];
    }

    /// <summary>
    /// A path Windows would not describe is <see cref="PathPresence.Refused"/>, never absent, so a
    /// folder that could not be reached is protected exactly as one that could. The content question
    /// stops at the first content it finds (G4). See <see cref="DirectoryContent"/>.
    /// </summary>
    internal static Before Probe(string path) => new(LongPath.ProbeEntry(path), DirectoryContent.IsPresent(path));

    /// <summary>What one protected path was before the run.</summary>
    internal readonly record struct Before(PathPresence Presence, bool HeldContent);
}
