using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// What the walk adds up: each root's tallies, one for each guard, and the folders it reached, which
/// carry which tallies' removals leave them standing.
/// </summary>
public sealed partial class ParallelEnumerationScanner
{
    /// <summary>
    /// Add one file or link to what each of <paramref name="tallies"/> takes from its directory, unless
    /// that tally's guard keeps it, which <paramref name="withheld"/> then records.
    ///
    /// <para>The walk hands over the timestamps the listing read, so asking an entry's age here costs
    /// no further I/O (G4). An entry the guard keeps contributes nothing, because the removal will not
    /// take it.</para>
    /// </summary>
    private static void Count(Tally[] tallies, Span<Counted> counted, long newestFileTime, long length, ref int withheld)
    {
        for (var i = 0; i < tallies.Length; i++)
        {
            if (tallies[i].Keep.Protects(newestFileTime))
            {
                withheld |= tallies[i].Bit;
                continue;
            }

            counted[i].Bytes += length;
            counted[i].Entries++;
        }
    }

    /// <summary>One root's readings, one <see cref="Tally"/> for each guard, added to from every thread the walk runs on.</summary>
    private sealed class RootTally(bool keepRoot, MinimumAge[] keeps)
    {
        public readonly bool KeepRoot = keepRoot;

        public readonly Tally[] Tallies = [.. keeps.Select((keep, i) => new Tally(keep, 1 << i))];

        public readonly ConcurrentBag<string> Stores = [];

        public bool RootRefused;

        /// <summary>The readings, read once the walk has ended.</summary>
        public Walked[] Readings(MinimumAge[] keeps)
        {
            if (RootRefused)
            {
                return Each(keeps, new Walked(ScanSize.Zero, false, [], RootReach.NotListed));
            }

            IReadOnlyList<string> found = [.. Stores.Order(StringComparer.OrdinalIgnoreCase)];

            return [.. Tallies.Select(tally => new Walked(
                new ScanSize(tally.Total, tally.Total, Entries: tally.Entries),
                tally.WithheldRecent,
                found,
                RootReach.Reached))];
        }

        /// <summary>Count one more folder left standing for each tally in <paramref name="tallies"/>.</summary>
        public void FolderStays(int tallies)
        {
            foreach (var tally in Tallies)
            {
                if ((tallies & tally.Bit) != 0)
                {
                    tally.FolderStays();
                }
            }
        }
    }

    /// <summary>What one directory's files and links add to one <see cref="Tally"/>.</summary>
    private struct Counted
    {
        public long Bytes;
        public long Entries;
    }

    /// <summary>
    /// What one guard's removal would take from the entries a walk lists, with <see cref="Bit"/>
    /// naming it in each <see cref="VisitedFolder"/>'s record of what stays.
    ///
    /// <para>Every folder is counted as it is reached, and counted back the first time it is marked
    /// as staying, so no folder is kept until the end to be counted. Added to from several threads at
    /// once, so every count moves through <see cref="Interlocked"/> and the flag through
    /// <see cref="Volatile"/>.</para>
    /// </summary>
    private sealed class Tally(MinimumAge keep, int bit)
    {
        private long _total;
        private long _entries;
        private long _staying;

        // Only ever moved to true, so a write needs no exchange and no ordering question arises.
        private bool _withheld;

        public MinimumAge Keep { get; } = keep;

        public int Bit { get; } = bit;

        public long Total => Interlocked.Read(ref _total);

        /// <summary>The files, links and folders the removal takes. Read once the walk has ended.</summary>
        public long Entries => Interlocked.Read(ref _entries) - Interlocked.Read(ref _staying);

        public bool WithheldRecent => Volatile.Read(ref _withheld);

        /// <summary>
        /// One directory: its folder and what <paramref name="counted"/> found in it, with
        /// <paramref name="withheld"/> naming the tallies whose guard kept something there.
        /// </summary>
        public void Add(Counted counted, int withheld)
        {
            if (counted.Bytes != 0)
            {
                Interlocked.Add(ref _total, counted.Bytes);
            }

            Interlocked.Add(ref _entries, counted.Entries + 1);

            // Read before written, so a tree full of recent files does not write the line again for
            // every directory once it is set.
            if ((withheld & Bit) != 0 && !Volatile.Read(ref _withheld))
            {
                Volatile.Write(ref _withheld, true);
            }
        }

        public void FolderStays() => Interlocked.Increment(ref _staying);
    }

    /// <summary>
    /// One folder the walk reached, and which tallies' removals would leave it standing, one bit
    /// for each <see cref="Tally"/>.
    ///
    /// <para>A folder stays while anything inside it stays, however deep, so a kept file has to mark
    /// every folder above it. The walk is parallel, so no folder's children are all known when it is
    /// reached; each folder instead holds the one above it, and a kept entry walks the
    /// chain upward. Nothing else holds a folder, so it can be collected once its children's work is
    /// done.</para>
    /// </summary>
    private sealed class VisitedFolder(VisitedFolder? holder, RootTally root)
    {
        private readonly VisitedFolder? _holder = holder;

        /// <summary>The readings of the root this folder was reached in.</summary>
        public RootTally Root { get; } = root;

        private int _stays;

        /// <summary>
        /// Mark this folder and every folder above it for each tally in <paramref name="tallies"/>,
        /// and count each folder for a tally the first time it is marked for it.
        ///
        /// <para>Above a folder, only the tallies this call was first to mark there go on, because
        /// whatever marked it for the others goes on to mark everything above it. Every folder in the
        /// chain was reached in the same root, so this folder's <see cref="Root"/> counts them
        /// all.</para>
        /// </summary>
        public void Stays(int tallies)
        {
            for (var folder = this; folder is not null && tallies != 0; folder = folder._holder)
            {
                tallies &= ~Interlocked.Or(ref folder._stays, tallies);
                Root.FolderStays(tallies);
            }
        }
    }
}
