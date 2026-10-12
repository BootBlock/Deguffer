using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Cloud;

/// <summary>
/// One sync root walked for the placeholders whose local copies may be released, and a tally of those
/// left as they are and why.
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
/// </summary>
public static class PlaceholderWalk
{
    /// <param name="threads">How many folders are read at once, the calling thread included.</param>
    public static ReleaseSelection Of(ICloudFiles cloud, string syncRoot, MinimumAge keep, int threads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);

        var tallies = new ConcurrentBag<Tally>();

        new WalkWorkers<PinState>(
                threads,
                descend =>
                {
                    var tally = new Tally(cloud, keep, descend, ct);
                    tallies.Add(tally);
                    return tally.Read;
                },
                onProgress: static _ => { },
                TimeProvider.System,
                ct)
            .Run(syncRoot, InheritedPins.Own(cloud, syncRoot, PinState.Unspecified));

        return Merged(tallies);
    }

    private static ReleaseSelection Merged(IEnumerable<Tally> tallies)
    {
        var files = new List<ReleasableFile>();
        var held = new Dictionary<HeldBack, HeldTally>();
        var unreadable = 0;

        foreach (var tally in tallies)
        {
            files.AddRange(tally.Files);
            unreadable += tally.Unreadable;

            foreach (var (reason, found) in tally.Held)
            {
                held[reason] = held.GetValueOrDefault(reason) + found;
            }
        }

        // Ordinal, so two names a case-sensitive folder tells apart keep one order between runs.
        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new ReleaseSelection(files, held, unreadable);
    }

    /// <summary>What one worker found in the folders it read, kept apart from the others' until the walk ends.</summary>
    private sealed class Tally(ICloudFiles cloud, MinimumAge keep, Action<string, PinState> descend, CancellationToken ct)
    {
        public List<ReleasableFile> Files { get; } = [];

        public Dictionary<HeldBack, HeldTally> Held { get; } = [];

        public int Unreadable { get; private set; }

        /// <param name="inherited">The pin the folder's own files inherit from it.</param>
        public void Read(string directory, PinState inherited)
        {
            foreach (var entry in cloud.List(directory, ct))
            {
                if (entry.IsOtherLink || !entry.IsPlaceholder)
                {
                    // A folder that is not a placeholder is still inside the root, and a sync app may
                    // keep placeholders below one. A link is the one thing never entered.
                    if (entry.IsDirectory && !entry.IsOtherLink)
                    {
                        descend(entry.Path, inherited);
                    }

                    continue;
                }

                var reading = cloud.Read(entry.Path);

                if (reading.Placeholder is not { } placeholder)
                {
                    Unreadable += reading.Presence is PathPresence.Refused ? 1 : 0;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    descend(entry.Path, ReleaseRules.PassedOn(placeholder.Pin, inherited));
                    continue;
                }

                switch (ReleaseRules.Hold(placeholder, inherited, keep))
                {
                    case null:
                        Files.Add(new ReleasableFile(entry.Path, placeholder.OnDiskBytes));
                        break;

                    case HeldBack.NothingOnDisk:
                        break;

                    case { } reason:
                        Held[reason] = Held.GetValueOrDefault(reason) + placeholder.OnDiskBytes;
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
