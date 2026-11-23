using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Cloud;

/// <summary>
/// Sync roots walked in one run for the placeholders whose local copies may be released, and for each
/// root a tally of those left as they are and why.
///
/// <para><b>What it opens, and what it never does.</b> Each folder is listed from its own index, and only
/// an entry that index calls a placeholder is opened, for its attributes. A file's data is never read, so
/// the walk cannot start a download.</para>
///
/// <para><b>Links are neither entered nor offered.</b> A junction or symbolic link inside a sync root
/// leads somewhere the sync app does not own, for the reason <see cref="Execution.DirectoryRemover"/>
/// does not follow one.</para>
///
/// <para><b>Folders are read in parallel, and the answer does not depend on it.</b> Describing a
/// placeholder costs an open, a describe and a close, so a root of a hundred thousand files read one
/// after another is several hundred thousand calls in a row. The folders go to the bounded workers of
/// <see cref="WalkWorkers{TState}"/>, each worker keeps its own tally, and the tallies are merged once
/// the walk is over: the counts are sums, and the files are put in order of path.</para>
///
/// <para><b>Several roots share one walk's threads.</b> Two roots on one volume walked one after the
/// other each pay a walk's start and its tail, while only one or two threads are busy. Walked side by
/// side as two walks, they would read the volume with twice the threads it is tuned for. In one run
/// they share its threads, and each folder carries the index of the root it is under, so each worker
/// tallies every root apart (#1071). <see cref="PlaceholderWalks"/> decides which roots share a run.</para>
/// </summary>
public static class PlaceholderWalk
{
    /// <summary>What each of <paramref name="syncRoots"/> holds, walked in one run, in the order given.</summary>
    /// <param name="threads">How many folders are read at once, the calling thread included.</param>
    public static IReadOnlyList<ReleaseSelection> Of(
        ICloudFiles cloud, IReadOnlyList<string> syncRoots, MinimumAge keep, int threads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);

        if (syncRoots.Count == 0)
        {
            return [];
        }

        var tallies = new ConcurrentBag<Tally>();

        new WalkWorkers<Reached>(
                threads,
                descend =>
                {
                    var tally = new Tally(cloud, keep, syncRoots.Count, descend, ct);
                    tallies.Add(tally);
                    return tally.Read;
                },
                onProgress: static _ => { },
                TimeProvider.System,
                ct)
            .Run([.. syncRoots.Select((root, at) => (root, new Reached(at, InheritedPins.Own(cloud, root, PinState.Unspecified))))]);

        return [.. Enumerable.Range(0, syncRoots.Count).Select(at => Merged(tallies, at))];
    }

    /// <summary>What every worker found under the root at <paramref name="root"/>, as one selection.</summary>
    private static ReleaseSelection Merged(IEnumerable<Tally> tallies, int root)
    {
        var files = new List<ReleasableFile>();
        var held = new Dictionary<HeldBack, HeldTally>();
        var unreadable = 0;

        foreach (var found in tallies.Select(tally => tally.Under(root)).OfType<Found>())
        {
            files.AddRange(found.Files);
            unreadable += found.Unreadable;

            foreach (var (reason, tally) in found.Held)
            {
                held[reason] = held.GetValueOrDefault(reason) + tally;
            }
        }

        // Ordinal, so two names a case-sensitive folder tells apart keep one order between runs.
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new ReleaseSelection(files, held, unreadable);
    }

    /// <summary>A folder waiting to be read: the root it is under, and the pin its own files inherit from it.</summary>
    private readonly record struct Reached(int Root, PinState Inherited);

    /// <summary>What one worker found under one root.</summary>
    private sealed class Found
    {
        public List<ReleasableFile> Files { get; } = [];

        public Dictionary<HeldBack, HeldTally> Held { get; } = [];

        public int Unreadable { get; set; }
    }

    /// <summary>
    /// What one worker found in the folders it read, kept apart from the others' until the walk ends, and
    /// apart for each root. A root's part is made when the worker first reads a folder under it.
    /// </summary>
    private sealed class Tally(ICloudFiles cloud, MinimumAge keep, int roots, Action<string, Reached> descend, CancellationToken ct)
    {
        private readonly Found?[] _found = new Found?[roots];

        public Found? Under(int root) => _found[root];

        public void Read(string directory, Reached reached)
        {
            var found = _found[reached.Root] ??= new Found();

            foreach (var entry in cloud.List(directory, ct))
            {
                if (entry.IsOtherLink || !entry.IsPlaceholder)
                {
                    // A folder that is not a placeholder is still inside the root, and a sync app may
                    // keep placeholders below one. A link is the one thing never entered.
                    if (entry.IsDirectory && !entry.IsOtherLink)
                    {
                        descend(entry.Path, reached);
                    }

                    continue;
                }

                var reading = cloud.Read(entry.Path);

                if (reading.Placeholder is not { } placeholder)
                {
                    found.Unreadable += reading.Presence is PathPresence.Refused ? 1 : 0;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    descend(entry.Path, reached with { Inherited = ReleaseRules.PassedOn(placeholder.Pin, reached.Inherited) });
                    continue;
                }

                switch (ReleaseRules.Hold(placeholder, reached.Inherited, keep))
                {
                    case null:
                        found.Files.Add(new ReleasableFile(entry.Path, placeholder.OnDiskBytes));
                        break;

                    case HeldBack.NothingOnDisk:
                        break;

                    case { } reason:
                        found.Held[reason] = found.Held.GetValueOrDefault(reason) + placeholder.OnDiskBytes;
                        break;
                }
            }
        }
    }
}

/// <summary>A placeholder chosen for release, and the bytes of it on this PC when it was chosen.</summary>
public sealed record ReleasableFile(string Path, long OnDiskBytes);

/// <summary>How many files one reason held back, and what they hold on this PC.</summary>
public readonly record struct HeldTally(int Files, long Bytes)
{
    public static HeldTally operator +(HeldTally tally, long bytes) => new(tally.Files + 1, tally.Bytes + bytes);

    public static HeldTally operator +(HeldTally tally, HeldTally other) => new(tally.Files + other.Files, tally.Bytes + other.Bytes);
}

/// <summary>What <see cref="PlaceholderWalk"/> found in one sync root.</summary>
/// <param name="Files">The placeholders that may be released.</param>
/// <param name="Held">Those with something on this PC that the rules left as they are, by reason.</param>
/// <param name="Unreadable">
/// Placeholders Windows would not describe, files and folders alike, which are left as they are with
/// everything inside them.
/// </param>
public sealed record ReleaseSelection(
    IReadOnlyList<ReleasableFile> Files,
    IReadOnlyDictionary<HeldBack, HeldTally> Held,
    int Unreadable)
{
    public long Bytes => Files.Sum(f => f.OnDiskBytes);

    public HeldTally HeldFor(HeldBack reason) => Held.GetValueOrDefault(reason);
}
