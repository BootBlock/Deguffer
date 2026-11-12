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
/// <para><b>What each path proves is unchanged.</b> Every path is recorded with the same two answers
/// it was given when each was asked alone, and the records come back in the order the paths were
/// given.</para>
///
/// <para><b>Not asked again where the row's own listing answered</b> (G5). A row that listed a
/// protected path's parent while it planned already knows the path was there, and sometimes whether it
/// held anything (<see cref="ListedEntries"/>). That answer is recorded as it stands, and describes the
/// moment the plan was made. Only what the listing left open is asked of the disk, and only those
/// paths take a slot of the disk's number.</para>
/// </summary>
internal static class ProtectedPathCapture
{
    /// <summary>
    /// What each of <paramref name="candidates"/> was, in their order, each path named once under the
    /// first reason given for it.
    /// </summary>
    /// <param name="tuning">What the walks of each volume run with. See <see cref="IDirectoryScanner.Tuning"/>.</param>
    /// <param name="listed">What the row's own listings saw while it planned, or null where it listed nothing worth reusing.</param>
    public static Task<IReadOnlyList<ProtectedPath>> CaptureAsync(
        IReadOnlyList<(string Path, string Reason)> candidates,
        ListedEntries? listed,
        ScanTuner tuning,
        CancellationToken ct) =>
        CaptureAsync(candidates, listed, tuning, Probe, DirectoryContent.IsPresent, ct);

    /// <summary>
    /// The same, with each unlisted path's two answers given by <paramref name="probe"/> and a listed
    /// path's open content question by <paramref name="content"/>: the seams a test blocks or counts
    /// the disk's answers through.
    /// </summary>
    internal static async Task<IReadOnlyList<ProtectedPath>> CaptureAsync(
        IReadOnlyList<(string Path, string Reason)> candidates,
        ListedEntries? listed,
        ScanTuner tuning,
        Func<string, Before> probe,
        Func<string, bool> content,
        CancellationToken ct)
    {
        (string Path, string Reason)[] named = [.. candidates.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase)];

        var found = new Before[named.Length];
        var asked = new List<int>(named.Length);

        for (var index = 0; index < named.Length; index++)
        {
            if (listed is not null && listed.TryGet(named[index].Path, out var heldContent) && heldContent is { } settled)
            {
                found[index] = new Before(PathPresence.Present, settled);
            }
            else
            {
                asked.Add(index);
            }
        }

        var answers = await DiskBoundWork.EachAsync(
            [.. asked.Select(index => named[index].Path)],
            tuning,
            path => listed is not null && listed.TryGet(path, out _) ? new Before(PathPresence.Present, content(path)) : probe(path),
            ct).ConfigureAwait(false);

        foreach (var (index, before) in asked.Zip(answers))
        {
            found[index] = before;
        }

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
